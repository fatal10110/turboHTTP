namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// A response body wrapper that only observes its <see cref="Inner"/> body (counts bytes, keeps a preview,
    /// reports completion) and passes everything else through unchanged: the same bytes from
    /// <c>ReadAsync</c>, the same <c>Length</c>, buffered data and trailers, and <c>Abort</c>/<c>DisposeAsync</c>
    /// on the inner body. The plugin capability guard looks through such a wrapper when it compares the body
    /// an observe-only plugin forwards with the one it received, so a pass-through observer is not reported
    /// as response mutation (#1235).
    /// </summary>
    /// <remarks>
    /// Internal on purpose: only package assemblies Core grants internals to can implement it, never a
    /// third-party plugin. An implementation that changes what a reader of the body sees would let an
    /// observe-only plugin mutate the response, so implement it only on a wrapper that forwards unchanged.
    /// </remarks>
    internal interface IPassThroughResponseBodySource
    {
        /// <summary>The wrapped body whose bytes this wrapper forwards unchanged.</summary>
        IResponseBodySource Inner { get; }
    }
}
