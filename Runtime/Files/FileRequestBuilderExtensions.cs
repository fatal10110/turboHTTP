using System;
using TurboHTTP.Core;

namespace TurboHTTP.Files
{
    public static class FileRequestBuilderExtensions
    {
        public static UHttpRequest WithFileBody(
            this UHttpRequest request,
            string path,
            int bufferSize = 32768)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            return request.WithContentInternal(new FileRequestBody(path, bufferSize));
        }
    }
}
