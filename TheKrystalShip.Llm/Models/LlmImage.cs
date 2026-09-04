namespace TheKrystalShip.Llm.Models;

/// <summary>
/// One image shown to the model alongside a message's text.
/// </summary>
/// <remarks>
/// <para>
/// The bytes are carried rather than a URL, because the two backends want the image inline and
/// neither of them fetches anything: llama.cpp takes a <c>data:</c> URL inside its OpenAI-shaped
/// content array, and Ollama takes bare base64 in its own <c>images</c> field. A caller that has a
/// link fetches it under its own rules — its own timeout, its own size cap, its own politeness to
/// whoever is serving the picture — and hands over what came back.
/// </para>
/// <para>
/// <see cref="MimeType"/> is the type the bytes actually are, as the server that served them said,
/// and it reaches the wire verbatim. A vision model told an image is a JPEG when it is a PNG decodes
/// nothing.
/// </para>
/// </remarks>
/// <param name="MimeType">The image's media type, such as <c>image/jpeg</c>.</param>
/// <param name="Bytes">The encoded image itself.</param>
public sealed record LlmImage(string MimeType, byte[] Bytes)
{
    /// <summary>The image base64-encoded, which is Ollama's <c>images</c> element.</summary>
    public string Base64() => Convert.ToBase64String(Bytes);

    /// <summary>The image as a <c>data:</c> URL, which is the OpenAI content part's <c>url</c>.</summary>
    public string DataUrl() => $"data:{MimeType};base64,{Base64()}";
}
