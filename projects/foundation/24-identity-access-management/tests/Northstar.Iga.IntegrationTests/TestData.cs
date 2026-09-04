using Microsoft.EntityFrameworkCore;
using Northstar.Iga.Domain;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.IntegrationTests;

public sealed record SeedData(
    UserIdentity User,
    UserIdentity Manager,
    UserIdentity OwnerOne,
    UserIdentity OwnerTwo,
    UserIdentity Security,
    TargetApplication Finance,
    TargetApplication Erp,
    TargetApplication Crm,
    Entitlement Low,
    Entitlement Medium,
    Entitlement Privileged,
    Entitlement ToxicA,
    Entitlement ToxicB);

public static class TestData
{
    public static async Task<SeedData> SeedAsync(
        IgaDbContext db,
        IdentityStatus userStatus = IdentityStatus.Active,
        string userDepartment = "Finance")
    {
        var manager = User("MGR-1", "Manager One", "manager@northstar.test", "Management", "People Manager");
        var ownerOne = User("OWN-1", "Owner One", "owner1@northstar.test", "Operations", "Application Owner");
        var ownerTwo = User("OWN-2", "Owner Two", "owner2@northstar.test", "Risk", "Risk Owner");
        var security = User("SEC-1", "Security One", "security@northstar.test", "Security", "Security Governance Lead");
        var user = User("USR-1", "Alice Synthetic", "alice@northstar.test", userDepartment, "Analyst");
        user.Status = userStatus;
        user.ManagerId = manager.Id;
        db.Users.AddRange(manager, ownerOne, ownerTwo, security, user);

        var finance = new TargetApplication { Key = "finance", Name = "Finance", Description = "Finance test target." };
        var erp = new TargetApplication { Key = "erp", Name = "ERP", Description = "ERP test target." };
        var crm = new TargetApplication { Key = "crm", Name = "CRM", Description = "CRM test target." };
        db.Applications.AddRange(finance, erp, crm);

        var low = Entitlement(finance.Id, "view-vendors", "app:finance/vendor:view", ownerOne.Id, RiskRating.Low);
        var medium = Entitlement(finance.Id, "edit-vendors", "app:finance/vendor:edit", ownerOne.Id, RiskRating.Medium);
        var privileged = Entitlement(
            finance.Id,
            "approve-payment",
            "app:finance/payment:approve",
            ownerTwo.Id,
            RiskRating.Critical,
            true);
        var toxicA = Entitlement(finance.Id, "create-vendor", "app:finance/vendor:create", ownerOne.Id, RiskRating.High);
        var toxicB = Entitlement(finance.Id, "release-payment", "app:finance/payment:release", ownerTwo.Id, RiskRating.High);
        db.Entitlements.AddRange(low, medium, privileged, toxicA, toxicB);
        await db.SaveChangesAsync();
        return new SeedData(
            user, manager, ownerOne, ownerTwo, security, finance, erp, crm, low, medium, privileged, toxicA, toxicB);
    }

    public static UserIdentity User(
        string employeeNumber,
        string name,
        string email,
        string department,
        string title) =>
        new()
        {
            EmployeeNumber = employeeNumber,
            DisplayName = name,
            Email = email,
            Department = department,
            JobTitle = title,
            Location = "Nairobi",
            CostCentre = $"CC-{department.ToUpperInvariant()}",
            EmploymentType = EmploymentType.Employee,
            Clearance = 3,
            StartDate = new DateOnly(2025, 1, 1),
            Status = IdentityStatus.Active
        };

    public static Entitlement Entitlement(
        Guid applicationId,
        string key,
        string permission,
        Guid ownerId,
        RiskRating risk,
        bool privileged = false) =>
        new()
        {
            ApplicationId = applicationId,
            Key = key,
            Permission = permission,
            OwnerUserId = ownerId,
            Risk = risk,
            IsPrivileged = privileged,
            BusinessDescription = $"Business reviewer description for {key}."
        };
}
