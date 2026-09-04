using Microsoft.AspNetCore.Mvc;
using Northstar.Legacy.Web.Legacy;
using Northstar.Legacy.Web.Models;

namespace Northstar.Legacy.Web.Controllers;

public sealed class AdministrationController : Controller
{
    public IActionResult Index()
    {
        var connectionString = AppSettings.Get("LegacyDatabase");
        return View(new LegacyAdministrationViewModel
        {
            Policyholders = DatabaseHelper.GetPolicyholders(connectionString),
            Policies = DatabaseHelper.GetPolicies(connectionString)
        });
    }

    [HttpPost]
    public IActionResult CreatePolicyholder(string name, string email)
    {
        DatabaseHelper.InsertPolicyholder(AppSettings.Get("LegacyDatabase"), name, email);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult CreatePolicy(string policyNumber, long policyholderId, decimal deductible, decimal policyLimit, string currency)
    {
        DatabaseHelper.InsertPolicy(AppSettings.Get("LegacyDatabase"), policyNumber, policyholderId, deductible, policyLimit, currency);
        return RedirectToAction(nameof(Index));
    }
}
