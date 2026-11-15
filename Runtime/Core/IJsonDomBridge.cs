namespace TurboHTTP.Core
{
    /// <summary>
    /// The document-model half of the JSON bridge (#1034): parses JSON into, and writes JSON from, the
    /// built-in LiteJson tree (<c>Dictionary&lt;string, object&gt;</c> objects, <c>List&lt;object&gt;</c>
    /// arrays, strings, numbers, <see cref="bool"/> and null). TurboHTTP.JSON implements it with
    /// <c>LiteJsonSerializer</c> whatever <c>JsonSerializer.Default</c> is, so internal consumers that walk
    /// the tree (record/replay files and redaction, OAuth token responses) never see a custom serializer's
    /// node types. User payloads keep going through <see cref="IJsonBridge"/>, which follows the default.
    /// A separate interface so an <see cref="IJsonBridge"/> without it keeps compiling; see
    /// <see cref="ProjectJsonBridge.GetRequiredDom"/>.
    /// </summary>
    internal interface IJsonDomBridge
    {
        /// <summary>
        /// Parses <paramref name="json"/> into the LiteJson tree.
        /// </summary>
        /// <param name="json">JSON text.</param>
        /// <param name="maxInputBytes">
        /// Largest accepted input in UTF-8 bytes. Values at or below LiteJson's default (16 MiB) use that
        /// default; a larger value raises the cap for this call only.
        /// </param>
        object ParseDom(string json, int maxInputBytes);

        /// <summary>Writes a LiteJson tree as JSON text.</summary>
        string WriteDom(object value);
    }
}
