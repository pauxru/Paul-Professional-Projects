using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed class DatabaseInitializer(IgaDbContext db, IClock clock) : IDatabaseInitializer
{
    private static readonly string[] Departments =
    [
        "Finance", "Technology", "Operations", "Sales", "Human Resources",
        "Legal", "Security", "Procurement", "Risk", "Customer Success"
    ];

    private static readonly string[] ApplicationKeys =
    [
        "finance", "erp", "crm", "billing", "procurement",
        "hris", "legal", "security-ops", "risk", "customer-success",
        "data-platform", "service-desk", "document-management", "travel", "payroll",
        "expenses", "vendor-portal", "analytics", "warehouse", "identity-provider",
        "source-control", "cloud-console", "learning", "facilities", "communications"
    ];

    public async Task InitializeAsync(bool seedDemoData, CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!seedDemoData || await db.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.UtcNow;
        var users = Enumerable.Range(1, 500)
            .Select(index =>
            {
                var department = Departments[(index - 1) % Departments.Length];
                var isLead = index <= Departments.Length;
                return new UserIdentity
                {
                    EmployeeNumber = $"NSG-{index:0000}",
                    DisplayName = $"Synthetic Identity {index:000}",
                    Email = $"synthetic.identity{index:000}@northstar.example",
                    Department = department,
                    JobTitle = isLead
                        ? department == "Security" ? "Security Governance Lead" : $"{department} Manager"
                        : $"{department} Specialist",
                    Location = index % 3 == 0 ? "London" : index % 2 == 0 ? "Johannesburg" : "Nairobi",
                    CostCentre = $"CC-{department.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant()[..Math.Min(4, department.Replace(" ", string.Empty, StringComparison.Ordinal).Length)]}",
                    EmploymentType = index % 13 == 0 ? EmploymentType.Contractor : EmploymentType.Employee,
                    Clearance = index % 7 == 0 ? 4 : index % 3 + 1,
                    StartDate = new DateOnly(2022 + index % 4, index % 12 + 1, index % 27 + 1),
                    Status = IdentityStatus.Active,
                    LastLoginAt = index % 11 == 0 ? now.AddDays(-120) : now.AddDays(-(index % 30))
                };
            })
            .ToList();
        var managers = users.Take(Departments.Length)
            .ToDictionary(x => x.Department, x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var user in users.Where(x => !managers.Values.Contains(x.Id)))
        {
            user.ManagerId = managers[user.Department];
        }
        db.Users.AddRange(users);

        var applications = ApplicationKeys.Select((key, index) => new TargetApplication
        {
            Key = key,
            Name = $"{ToTitle(key)} Application",
            Description = $"Synthetic Northstar Group (fictional) application catalogue entry for {ToTitle(key)}.",
            OwnerUserId = users[(index + 20) % users.Count].Id
        }).ToList();
        db.Applications.AddRange(applications);

        var entitlements = new List<Entitlement>();
        foreach (var application in applications)
        {
            entitlements.Add(new Entitlement
            {
                ApplicationId = application.Id,
                Key = $"{application.Key}-view",
                Permission = $"app:{application.Key}/records:view",
                OwnerUserId = application.OwnerUserId,
                Risk = RiskRating.Low,
                BusinessDescription = $"View business records in {application.Name}; this does not permit changes."
            });
            entitlements.Add(new Entitlement
            {
                ApplicationId = application.Id,
                Key = $"{application.Key}-administer",
                Permission = $"app:{application.Key}/configuration:administer",
                OwnerUserId = application.OwnerUserId,
                Risk = RiskRating.High,
                BusinessDescription = $"Change security-sensitive configuration in {application.Name}.",
                IsPrivileged = true
            });
        }

        var financeApplication = applications.Single(x => x.Key == "finance");
        var createVendor = new Entitlement
        {
            ApplicationId = financeApplication.Id,
            Key = "create-vendor",
            Permission = "app:finance/vendor:create",
            OwnerUserId = managers["Finance"],
            Risk = RiskRating.High,
            BusinessDescription = "Create a supplier record that can later receive payments."
        };
        var approvePayment = new Entitlement
        {
            ApplicationId = financeApplication.Id,
            Key = "approve-payment",
            Permission = "app:finance/payment:approve",
            OwnerUserId = managers["Risk"],
            Risk = RiskRating.Critical,
            BusinessDescription = "Release a supplier payment after validating its supporting evidence.",
            IsPrivileged = true
        };
        entitlements.Add(createVendor);
        entitlements.Add(approvePayment);
        db.Entitlements.AddRange(entitlements);

        var baseReader = new Role
        {
            Key = "employee-base-reader",
            Name = "Employee Base Reader",
            Description = "Common read-only access required by active employees.",
            IsBirthright = true,
            BirthrightRule = "employmentType == Employee && status == Active"
        };
        var financeOperator = new Role
        {
            Key = "finance-operator",
            Name = "Finance Operator",
            Description = "Operational supplier-maintenance access for Finance employees.",
            IsBirthright = true,
            BirthrightRule = "department == Finance && employmentType == Employee"
        };
        var financeSenior = new Role
        {
            Key = "finance-senior",
            Name = "Finance Senior",
            Description = "Senior Finance access inheriting the Finance Operator role."
        };
        var financeApprover = new Role
        {
            Key = "finance-payment-approver",
            Name = "Finance Payment Approver",
            Description = "High-risk payment approval role."
        };
        var technologyReader = new Role
        {
            Key = "technology-reader",
            Name = "Technology Reader",
            Description = "Read access used by the dynamic Technology employee group."
        };
        var platformOperator = new Role
        {
            Key = "platform-operator",
            Name = "Platform Operator",
            Description = "Intermediate role in the demonstrative hierarchy."
        };
        var platformSenior = new Role
        {
            Key = "platform-senior",
            Name = "Platform Senior",
            Description = "Top-level role demonstrating deep transitive inheritance."
        };
        var roles = new[]
        {
            baseReader, financeOperator, financeSenior, financeApprover, technologyReader, platformOperator, platformSenior
        };
        db.Roles.AddRange(roles);

        var firstViewEntitlements = entitlements
            .Where(x => x.Key.EndsWith("-view", StringComparison.Ordinal))
            .Take(3)
            .ToArray();
        db.RoleEntitlements.AddRange(firstViewEntitlements.Select(x => new RoleEntitlement
        {
            RoleId = baseReader.Id,
            EntitlementId = x.Id
        }));
        db.RoleEntitlements.Add(new RoleEntitlement { RoleId = financeOperator.Id, EntitlementId = createVendor.Id });
        db.RoleEntitlements.Add(new RoleEntitlement { RoleId = financeApprover.Id, EntitlementId = approvePayment.Id });
        var sourceControlView = entitlements.Single(x => x.Key == "source-control-view");
        db.RoleEntitlements.Add(new RoleEntitlement { RoleId = technologyReader.Id, EntitlementId = sourceControlView.Id });
        db.RoleInheritances.AddRange(
            new RoleInheritance { RoleId = financeSenior.Id, InheritedRoleId = financeOperator.Id },
            new RoleInheritance { RoleId = technologyReader.Id, InheritedRoleId = baseReader.Id },
            new RoleInheritance { RoleId = platformOperator.Id, InheritedRoleId = technologyReader.Id },
            new RoleInheritance { RoleId = platformSenior.Id, InheritedRoleId = platformOperator.Id });

        var technologyGroup = new Group
        {
            Key = "technology-employees",
            Name = "Technology Employees",
            Description = "Dynamic group derived from department and employment type.",
            Type = GroupType.Dynamic,
            DynamicRule = "department == Technology && employmentType == Employee"
        };
        db.Groups.Add(technologyGroup);
        db.GroupRoles.Add(new GroupRole { GroupId = technologyGroup.Id, RoleId = technologyReader.Id });

        foreach (var user in users)
        {
            db.UserRoleGrants.Add(new UserRoleGrant
            {
                UserId = user.Id,
                RoleId = baseReader.Id,
                Source = GrantSource.Birthright,
                GrantedAt = now,
                Reason = "Seeded birthright grant for the fictional demonstration estate."
            });
            if (user.Department == "Finance" && user.EmploymentType == EmploymentType.Employee)
            {
                db.UserRoleGrants.Add(new UserRoleGrant
                {
                    UserId = user.Id,
                    RoleId = financeOperator.Id,
                    Source = GrantSource.Birthright,
                    GrantedAt = now,
                    Reason = "Seeded Finance birthright grant."
                });
            }
            if (DynamicGroupRuleEvaluator.IsMatch(user, technologyGroup.DynamicRule))
            {
                db.GroupMembers.Add(new GroupMember
                {
                    GroupId = technologyGroup.Id,
                    UserId = user.Id,
                    Source = GroupMembershipSource.Dynamic,
                    AddedAt = now
                });
            }
        }

        db.Policies.AddRange(
            new PolicyDefinition
            {
                Name = "Deny privileged operations from untrusted devices",
                Description = "Privileged operations require a trusted managed device.",
                Effect = PolicyEffect.Deny,
                PermissionPattern = "app:*/configuration:administer",
                Priority = 1000,
                ConditionsJson = """{"environment":{"deviceTrust":"Untrusted"}}"""
            },
            new PolicyDefinition
            {
                Name = "Deny critical Finance approval without phishing-resistant MFA",
                Description = "Payment approval requires MFA level 2 or higher.",
                Effect = PolicyEffect.Deny,
                PermissionPattern = "app:finance/payment:approve",
                Priority = 1100,
                ConditionsJson = """{"environment":{"mfaLevel":"<2"}}"""
            },
            new PolicyDefinition
            {
                Name = "Finance corporate-network ABAC allow",
                Description = "Finance employees may perform vendor operations on the corporate network with MFA.",
                Effect = PolicyEffect.Allow,
                PermissionPattern = "app:finance/vendor:*",
                Priority = 200,
                ConditionsJson = """{"subject":{"department":"Finance","employmentType":"Employee"},"environment":{"networkZone":"Corporate","mfaLevel":">=2"}}"""
            });
        db.SoDRules.Add(new SoDRule
        {
            Name = "Vendor creation and payment approval",
            EntitlementAId = createVendor.Id,
            EntitlementBId = approvePayment.Id,
            Severity = RiskRating.Critical,
            BusinessDescription = "One person must not both create a supplier and approve payments to that supplier."
        });

        var details = JsonSerializer.Serialize(new { users = 500, applications = 25, fictional = true });
        var recordText = $"1|{now:O}|system|seed.completed|system|northstar-group|seed|{details}";
        db.AuditRecords.Add(new AuditRecord
        {
            Sequence = 1,
            Timestamp = now,
            Actor = "system",
            Action = "seed.completed",
            ResourceType = "system",
            ResourceId = "northstar-group",
            CorrelationId = "seed",
            BeforeHash = Hash(string.Empty),
            AfterHash = Hash(details),
            DetailsJson = details,
            PreviousHash = string.Empty,
            RecordHash = Hash(recordText)
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string ToTitle(string key) =>
        string.Join(' ', key.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
