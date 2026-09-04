namespace Lab.Application.Abstractions;

public interface ITokenIssuer
{
    string Issue(string subject, IReadOnlyCollection<string> scopes);
}
