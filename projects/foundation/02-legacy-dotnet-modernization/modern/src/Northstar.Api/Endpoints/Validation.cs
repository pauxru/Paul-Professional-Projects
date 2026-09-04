using System.ComponentModel.DataAnnotations;

namespace Northstar.Api.Endpoints;

public static class Validation
{
    public static Dictionary<string, string[]> Errors(object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
                .Select(member => new { member, ErrorMessage = result.ErrorMessage ?? "Invalid value." }))
            .GroupBy(item => item.member)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ErrorMessage).ToArray());
    }
}
