using System;
using System.Reflection;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Late-bound bridge to TurboHTTP.JSON so assemblies can remain optional.
    /// Reflection metadata is cached to avoid repeated lookup per call.
    /// </summary>
    public static class ProjectJsonBridge
    {
        private const string SerializerTypeName = "TurboHTTP.JSON.JsonSerializer, TurboHTTP.JSON";

        private static readonly object Gate = new object();
        private static Type _serializerType;
        private static MethodInfo _deserializeMethod;
        private static MethodInfo _serializeMethod;

        public static object Deserialize(string json, Type payloadType, string requiredBy)
        {
            if (payloadType == null) throw new ArgumentNullException(nameof(payloadType));
            var method = ResolveDeserializeMethod(requiredBy);

            try
            {
                return method.Invoke(null, new object[] { json, payloadType });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialize JSON payload for {requiredBy}.",
                    tie.InnerException);
            }
        }

        public static string Serialize(object payload, Type payloadType, string requiredBy)
        {
            if (payloadType == null) throw new ArgumentNullException(nameof(payloadType));
            var method = ResolveSerializeMethod(requiredBy);

            try
            {
                return (string)method.Invoke(null, new[] { payload, payloadType });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new InvalidOperationException(
                    $"Failed to serialize JSON payload for {requiredBy}.",
                    tie.InnerException);
            }
        }

        private static MethodInfo ResolveDeserializeMethod(string requiredBy)
        {
            lock (Gate)
            {
                if (_deserializeMethod != null)
                    return _deserializeMethod;

                var serializerType = ResolveSerializerType_NoLock(requiredBy);
                _deserializeMethod = serializerType.GetMethod(
                    "Deserialize",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), typeof(Type) },
                    modifiers: null);

                if (_deserializeMethod == null)
                {
                    throw new InvalidOperationException(
                        "TurboHTTP.JSON.JsonSerializer.Deserialize(string, Type) was not found.");
                }

                return _deserializeMethod;
            }
        }

        private static MethodInfo ResolveSerializeMethod(string requiredBy)
        {
            lock (Gate)
            {
                if (_serializeMethod != null)
                    return _serializeMethod;

                var serializerType = ResolveSerializerType_NoLock(requiredBy);
                _serializeMethod = serializerType.GetMethod(
                    "Serialize",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(object), typeof(Type) },
                    modifiers: null);

                if (_serializeMethod == null)
                {
                    throw new InvalidOperationException(
                        "TurboHTTP.JSON.JsonSerializer.Serialize(object, Type) was not found.");
                }

                return _serializeMethod;
            }
        }

        private static Type ResolveSerializerType_NoLock(string requiredBy)
        {
            if (_serializerType != null)
                return _serializerType;

            _serializerType = Type.GetType(SerializerTypeName, throwOnError: false);
            if (_serializerType == null)
            {
                throw new InvalidOperationException(
                    "TurboHTTP.JSON assembly is required for " + requiredBy + ". " +
                    "Install/enable TurboHTTP.JSON and ensure it is loaded.");
            }

            return _serializerType;
        }
    }
}
