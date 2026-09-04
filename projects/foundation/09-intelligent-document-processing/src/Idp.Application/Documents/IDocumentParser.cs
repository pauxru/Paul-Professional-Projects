namespace Idp.Application.Documents;

/// <summary>Parses raw uploaded bytes into a layout-aware <see cref="DocumentContent"/>.</summary>
public interface IDocumentParser
{
    /// <summary>True if this parser recognises the given file name / content type.</summary>
    bool CanParse(string fileName, string contentType);

    DocumentContent Parse(string fileName, string contentType, byte[] bytes);
}
