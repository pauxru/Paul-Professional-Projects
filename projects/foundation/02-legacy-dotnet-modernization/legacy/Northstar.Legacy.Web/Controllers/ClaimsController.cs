using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Northstar.Legacy.Web.Legacy;
using Northstar.Legacy.Web.Models;
using Northstar.Legacy.Web.Services;

namespace Northstar.Legacy.Web.Controllers;

public sealed class ClaimsController : Controller
{
    public IActionResult Index(string? policyholder)
    {
        var connectionString = AppSettings.Get("LegacyDatabase");
        Thread.Sleep(15);
        var claims = string.IsNullOrWhiteSpace(policyholder)
            ? DatabaseHelper.GetClaims(connectionString)
            : DatabaseHelper.FindClaimsByPolicyholderUnsafe(connectionString, policyholder);
        ViewBag.Policyholder = policyholder;
        ViewBag.OpenClaimCount = GetOpenClaimCountForBanner();
        ViewBag.LastRefreshed = DateTime.Now.ToString("dddd, dd MMM yyyy HH:mm");
        return View(claims);
    }

    [HttpGet]
    public IActionResult Details(long id)
    {
        var claim = DatabaseHelper.FindClaim(AppSettings.Get("LegacyDatabase"), id);
        if (claim is null)
        {
            return NotFound();
        }

        ViewBag.PotentialSettlement = LegacySettlementCalculator.Calculate(claim.ClaimedAmount, claim.Deductible, claim.PolicyLimit).ToString("N2");
        return View(claim);
    }

    [HttpGet]
    public IActionResult Create() => View(new LegacyClaimIntakeForm());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Create(LegacyClaimIntakeForm form)
    {
        try
        {
            var policy = DatabaseHelper.FindPolicy(AppSettings.Get("LegacyDatabase"), form.PolicyNumber);
            if (policy is null)
            {
                ModelState.AddModelError("PolicyNumber", "Policy not found.");
                return View(form);
            }

            var generatedReference = LegacyServiceLocator.GetReferenceGenerator().Generate();
            var reserve = Task.Run(() => LegacySettlementCalculator.Calculate(form.ClaimedAmount, policy.Deductible, policy.PolicyLimit)).Result;
            var claim = new LegacyClaim
            {
                ClaimReference = generatedReference,
                PolicyId = policy.Id,
                PolicyholderName = policy.PolicyholderName,
                Status = "Submitted",
                ClaimedAmount = form.ClaimedAmount,
                Deductible = policy.Deductible,
                PolicyLimit = policy.PolicyLimit,
                ReserveAmount = reserve,
                Currency = form.Currency,
                CreatedUtc = DateTime.UtcNow
            };
            var id = DatabaseHelper.InsertClaim(AppSettings.Get("LegacyDatabase"), claim);
            HttpContext.Session.SetString("CurrentClaimId", id.ToString());
            Console.WriteLine($"Legacy claim created: {generatedReference}");
            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception)
        {
            Trace.Write(exception);
            ModelState.AddModelError(string.Empty, "The claim could not be saved. Please retry.");
            return View(form);
        }
    }

    [HttpPost]
    public IActionResult Assess(long id, string adjuster, decimal reserveAmount, string status)
    {
        // Controller owns validation, workflow decisions, persistence and display concerns: intentional legacy shape.
        if (reserveAmount < 0)
        {
            TempData["Error"] = "Reserve cannot be negative.";
            return RedirectToAction(nameof(Index));
        }

        if (status is not ("UnderReview" or "Approved" or "Rejected" or "Settled" or "Closed"))
        {
            TempData["Error"] = "Unknown workflow stage.";
            return RedirectToAction(nameof(Index));
        }

        DatabaseHelper.UpdateAssessment(AppSettings.Get("LegacyDatabase"), id, adjuster, reserveAmount, status);
        HttpContext.Session.SetString("CurrentClaimId", id.ToString());
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult AttachDocument(long id, IFormFile? document)
    {
        try
        {
            if (document is not null && document.Length > 0)
            {
                var folder = Path.Combine(AppSettings.Get("DocumentFolder"), id.ToString());
                Directory.CreateDirectory(folder);
                var destination = Path.Combine(folder, $"{DateTime.Now.Ticks}-{document.FileName}");
                using var stream = System.IO.File.Create(destination);
                document.CopyTo(stream);
                DatabaseHelper.AddDocument(AppSettings.Get("LegacyDatabase"), id, document.FileName, destination);
            }
        }
        catch (Exception)
        {
            // LEGACY-SMELL: attachment failures are swallowed, leaving callers unable to distinguish success.
        }

        return RedirectToAction(nameof(Index));
    }

    private static int GetOpenClaimCountForBanner()
    {
        // LEGACY-SMELL: controller opens a connection and embeds SQL instead of delegating a query.
        using var connection = new SqliteConnection(AppSettings.Get("LegacyDatabase"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Claims WHERE Status <> 'Closed';";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
