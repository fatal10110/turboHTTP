#!/bin/bash
# repackage_bouncycastle.sh
# Repackages BouncyCastle source with TurboHTTP namespace prefix
# Only copies the folders needed for TLS

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Source can be in either location - check both
if [ -d "${PROJECT_ROOT}/Assets/TurboHTTP/ThirdParty/BouncyCastle-Source" ]; then
    SOURCE_DIR="${PROJECT_ROOT}/Assets/TurboHTTP/ThirdParty/BouncyCastle-Source"
elif [ -d "${PROJECT_ROOT}/ThirdParty/BouncyCastle-Source" ]; then
    SOURCE_DIR="${PROJECT_ROOT}/ThirdParty/BouncyCastle-Source"
else
    SOURCE_DIR="${PROJECT_ROOT}/ThirdParty/BouncyCastle-Source"  # Default for error message
fi
TARGET_DIR="${PROJECT_ROOT}/Runtime/Transport/BouncyCastle/Lib"

# Only these folders are needed for TLS
REQUIRED_FOLDERS="tls crypto asn1 x509 math security util"

echo "=== BouncyCastle Repackager ==="
echo "Source: $SOURCE_DIR"
echo "Target: $TARGET_DIR"
echo "Required folders: $REQUIRED_FOLDERS"
echo ""

# Check if source directory exists
if [ ! -d "$SOURCE_DIR" ]; then
    echo "ERROR: Source directory not found!"
    echo ""
    echo "Please download BouncyCastle source first:"
    echo "  1. git clone https://github.com/bcgit/bc-csharp.git /tmp/bc-csharp"
    echo "  2. cd /tmp/bc-csharp && git checkout release-2.2"
    echo "  3. mkdir -p '$SOURCE_DIR'"
    echo "  4. cp -r /tmp/bc-csharp/crypto/src/* '$SOURCE_DIR/'"
    echo ""
    echo "Then run this script again."
    exit 1
fi

# Clean target directory
rm -rf "$TARGET_DIR"
mkdir -p "$TARGET_DIR"

# Process only required folders
for folder in $REQUIRED_FOLDERS; do
    if [ -d "$SOURCE_DIR/$folder" ]; then
        echo "Processing $folder..."
        
        find "$SOURCE_DIR/$folder" -name "*.cs" | while read -r file; do
            # Calculate relative path from source folder
            REL_PATH="${file#$SOURCE_DIR/}"
            TARGET_FILE="$TARGET_DIR/$REL_PATH"
            TARGET_SUBDIR="$(dirname "$TARGET_FILE")"
            
            mkdir -p "$TARGET_SUBDIR"
            
            # Replace namespaces and using statements
            sed -e 's/namespace Org\.BouncyCastle/namespace TurboHTTP.SecureProtocol.Org.BouncyCastle/g' \
                -e 's/using Org\.BouncyCastle/using TurboHTTP.SecureProtocol.Org.BouncyCastle/g' \
                "$file" > "$TARGET_FILE"
        done
    else
        echo "WARNING: Folder '$folder' not found in source"
    fi
done

# Copy AssemblyInfo.cs if it exists
if [ -f "$SOURCE_DIR/AssemblyInfo.cs" ]; then
    sed -e 's/namespace Org\.BouncyCastle/namespace TurboHTTP.SecureProtocol.Org.BouncyCastle/g' \
        -e 's/using Org\.BouncyCastle/using TurboHTTP.SecureProtocol.Org.BouncyCastle/g' \
        "$SOURCE_DIR/AssemblyInfo.cs" > "$TARGET_DIR/AssemblyInfo.cs"
fi

# Count files
FILE_COUNT=$(find "$TARGET_DIR" -name "*.cs" | wc -l | tr -d ' ')

echo ""
echo "=== Repackaging Complete ==="
echo "Processed $FILE_COUNT files"
echo "Output: $TARGET_DIR"
echo ""
echo "You can now delete the original source:"
echo "  rm -rf '$SOURCE_DIR'"
echo ""
