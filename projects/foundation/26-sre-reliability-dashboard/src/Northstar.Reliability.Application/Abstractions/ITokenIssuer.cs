namespace Northstar.Reliability.Application.Abstractions;

public interface ITokenIssuer
{
    string Issue(string subject, IEnumerable<string> scopes);
}
