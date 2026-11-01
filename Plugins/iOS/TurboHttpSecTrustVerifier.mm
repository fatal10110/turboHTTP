// Server certificate path validation for the BouncyCastle TLS provider (#239), called from
// Runtime/Transport/Tls/AppleTrustVerifier.cs through DllImport("__Internal").
//
// Security.framework and Foundation only (no UIKit), so a macOS player can reuse this file (#248).
// Core Foundation objects only, released explicitly: the file builds with or without ARC.
//
// Policy: SecPolicyCreateSSL(server, host) against the system trust store, network fetch disabled
// (no AIA intermediates, no OCSP/CRL), at the caller's verification time. Fails closed: every
// error path returns a non-zero result and no path.
//
// Result buffer (only on kTurboTrustTrusted): malloc'd, native-endian int32 count, then per
// certificate int32 length and the DER bytes, leaf first. The caller releases it once with
// TurboHttp_SecTrustFree.

#import <Foundation/Foundation.h>
#import <Security/Security.h>

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// Keep in sync with AppleTrustVerifier.Result* in C#.
enum
{
    kTurboTrustTrusted = 0,
    kTurboTrustUntrustedRoot = 1,
    kTurboTrustNotTimeValid = 2,
    kTurboTrustInvalid = 3,
    kTurboTrustBridgeError = 4,
};

// PlatformVerifierRunner.MaxPathLength (11) with headroom; the C# side enforces the real bound.
static const CFIndex kTurboTrustMaxCertificates = 16;
// Largest certificate accepted in either direction.
static const int32_t kTurboTrustMaxCertificateBytes = 64 * 1024;

namespace
{
    // Owns one Core Foundation reference; released once when the scope ends.
    struct CFHolder
    {
        CFTypeRef ref;
        explicit CFHolder(CFTypeRef r = NULL) : ref(r) {}
        ~CFHolder() { if (ref != NULL) CFRelease(ref); }
        CFHolder(const CFHolder&) = delete;
        CFHolder& operator=(const CFHolder&) = delete;
    };

    int32_t MapEvaluationError(CFErrorRef error, int32_t* outStatus)
    {
        CFIndex code = error != NULL ? CFErrorGetCode(error) : 0;
        *outStatus = (int32_t)code;
        switch (code)
        {
            case errSecCertificateExpired:
            case errSecCertificateNotValidYet:
                return kTurboTrustNotTimeValid;
            case errSecNotTrusted:
                return kTurboTrustUntrustedRoot;
            default:
                // Host-name mismatch (errSecHostNameMismatch), usage, constraints, signatures.
                return kTurboTrustInvalid;
        }
    }

    // The built chain, leaf first, as a +1 array.
    CFArrayRef CopyBuiltChain(SecTrustRef trust)
    {
        if (@available(iOS 15.0, macOS 12.0, tvOS 15.0, *))
            return SecTrustCopyCertificateChain(trust);

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        CFIndex count = SecTrustGetCertificateCount(trust);
        if (count <= 0 || count > kTurboTrustMaxCertificates)
            return NULL;
        CFMutableArrayRef chain = CFArrayCreateMutable(kCFAllocatorDefault, count, &kCFTypeArrayCallBacks);
        if (chain == NULL)
            return NULL;
        for (CFIndex i = 0; i < count; i++)
        {
            SecCertificateRef cert = SecTrustGetCertificateAtIndex(trust, i);  // not retained
            if (cert == NULL)
            {
                CFRelease(chain);
                return NULL;
            }
            CFArrayAppendValue(chain, cert);
        }
        return chain;
#pragma clang diagnostic pop
    }

    bool CopyPathBuffer(SecTrustRef trust, uint8_t** outPath, int32_t* outPathLength)
    {
        CFHolder chain(CopyBuiltChain(trust));
        if (chain.ref == NULL)
            return false;

        CFArrayRef certs = (CFArrayRef)chain.ref;
        CFIndex count = CFArrayGetCount(certs);
        if (count <= 0 || count > kTurboTrustMaxCertificates)
            return false;

        CFDataRef data[kTurboTrustMaxCertificates] = {};
        bool ok = true;
        size_t total = sizeof(int32_t);
        for (CFIndex i = 0; i < count && ok; i++)
        {
            data[i] = SecCertificateCopyData((SecCertificateRef)CFArrayGetValueAtIndex(certs, i));
            CFIndex length = data[i] != NULL ? CFDataGetLength(data[i]) : 0;
            if (length <= 0 || length > kTurboTrustMaxCertificateBytes)
                ok = false;
            else
                total += sizeof(int32_t) + (size_t)length;
        }

        uint8_t* buffer = ok ? (uint8_t*)malloc(total) : NULL;
        if (buffer != NULL)
        {
            int32_t count32 = (int32_t)count;
            memcpy(buffer, &count32, sizeof(count32));
            size_t offset = sizeof(int32_t);
            for (CFIndex i = 0; i < count; i++)
            {
                int32_t length = (int32_t)CFDataGetLength(data[i]);
                memcpy(buffer + offset, &length, sizeof(length));
                offset += sizeof(length);
                memcpy(buffer + offset, CFDataGetBytePtr(data[i]), (size_t)length);
                offset += (size_t)length;
            }
            *outPath = buffer;
            *outPathLength = (int32_t)total;
        }

        for (CFIndex i = 0; i < count; i++)
        {
            if (data[i] != NULL)
                CFRelease(data[i]);
        }
        return buffer != NULL;
    }

    int32_t Evaluate(const uint8_t* chainBytes, const int32_t* lengths, int32_t count, const char* host,
                     double verifyTimeUnixSeconds, uint8_t** outPath, int32_t* outPathLength, int32_t* outStatus)
    {
        CFHolder certs(CFArrayCreateMutable(kCFAllocatorDefault, count, &kCFTypeArrayCallBacks));
        if (certs.ref == NULL)
            return kTurboTrustBridgeError;

        size_t offset = 0;
        for (int32_t i = 0; i < count; i++)
        {
            int32_t length = lengths[i];
            if (length <= 0 || length > kTurboTrustMaxCertificateBytes)
                return kTurboTrustInvalid;

            CFHolder data(CFDataCreate(kCFAllocatorDefault, chainBytes + offset, length));
            offset += (size_t)length;
            if (data.ref == NULL)
                return kTurboTrustBridgeError;

            CFHolder cert(SecCertificateCreateWithData(kCFAllocatorDefault, (CFDataRef)data.ref));
            if (cert.ref == NULL)
                return kTurboTrustInvalid;  // not a DER certificate
            CFArrayAppendValue((CFMutableArrayRef)certs.ref, cert.ref);
        }

        CFHolder hostName(CFStringCreateWithCString(kCFAllocatorDefault, host, kCFStringEncodingUTF8));
        if (hostName.ref == NULL)
            return kTurboTrustInvalid;

        CFHolder policy(SecPolicyCreateSSL(true, (CFStringRef)hostName.ref));
        if (policy.ref == NULL)
            return kTurboTrustBridgeError;

        SecTrustRef trustRef = NULL;
        OSStatus status = SecTrustCreateWithCertificates(certs.ref, policy.ref, &trustRef);
        CFHolder trust(trustRef);
        if (status != errSecSuccess || trust.ref == NULL)
        {
            *outStatus = (int32_t)status;
            return kTurboTrustBridgeError;
        }

        // No network: evaluation runs synchronously inside the TLS handshake (#142).
        status = SecTrustSetNetworkFetchAllowed((SecTrustRef)trust.ref, false);
        if (status != errSecSuccess)
        {
            *outStatus = (int32_t)status;
            return kTurboTrustBridgeError;
        }

        CFHolder date(CFDateCreate(kCFAllocatorDefault, verifyTimeUnixSeconds - kCFAbsoluteTimeIntervalSince1970));
        if (date.ref == NULL)
            return kTurboTrustBridgeError;
        status = SecTrustSetVerifyDate((SecTrustRef)trust.ref, (CFDateRef)date.ref);
        if (status != errSecSuccess)
        {
            *outStatus = (int32_t)status;
            return kTurboTrustBridgeError;
        }

        CFErrorRef errorRef = NULL;
        bool trusted = SecTrustEvaluateWithError((SecTrustRef)trust.ref, &errorRef);
        CFHolder error(errorRef);
        if (!trusted)
            return MapEvaluationError(errorRef, outStatus);

        return CopyPathBuffer((SecTrustRef)trust.ref, outPath, outPathLength)
            ? kTurboTrustTrusted
            : kTurboTrustBridgeError;
    }
}

extern "C" int32_t TurboHttp_SecTrustEvaluate(const uint8_t* chainBytes, const int32_t* lengths, int32_t count,
                                              const char* host, double verifyTimeUnixSeconds,
                                              uint8_t** outPath, int32_t* outPathLength, int32_t* outStatus)
{
    if (outPath == NULL || outPathLength == NULL || outStatus == NULL)
        return kTurboTrustBridgeError;
    *outPath = NULL;
    *outPathLength = 0;
    *outStatus = 0;

    if (chainBytes == NULL || lengths == NULL || count <= 0 || count > kTurboTrustMaxCertificates
        || host == NULL || host[0] == '\0')
        return kTurboTrustBridgeError;

    @autoreleasepool
    {
        int32_t result = Evaluate(chainBytes, lengths, count, host, verifyTimeUnixSeconds,
                                  outPath, outPathLength, outStatus);
        if (result != kTurboTrustTrusted && *outPath != NULL)
        {
            free(*outPath);
            *outPath = NULL;
            *outPathLength = 0;
        }
        return result;
    }
}

extern "C" void TurboHttp_SecTrustFree(void* buffer)
{
    free(buffer);
}
