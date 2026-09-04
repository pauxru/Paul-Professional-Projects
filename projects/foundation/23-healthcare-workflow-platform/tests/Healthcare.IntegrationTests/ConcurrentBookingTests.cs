using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Healthcare.IntegrationTests;

public class ConcurrentBookingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ConcurrentBookingTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Two_Concurrent_Bookings_For_The_Same_Slot_Only_One_Succeeds()
    {
        var seed = await TestSeed.SeedAsync(_factory);
        var slotUtc = new DateTimeOffset(2026, 9, 7, 6, 0, 0, TimeSpan.Zero);

        // Attempt two concurrent bookings via the service (not HTTP), each using its own scope
        // (i.e. its own DbContext) so that they race at the database.
        async Task<Result> Attempt(int attempt)
        {
            using var scope = _factory.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<AppointmentBookingService>();
            try
            {
                var appt = await svc.BookAsync(new BookAppointmentCommand(
                    attempt == 1 ? seed.PatientId : seed.Patient2Id,
                    seed.ClinicianId, seed.FacilityId, seed.RoomId, seed.GpTypeId, slotUtc),
                    "test-user", "Receptionist", "test-corr", CancellationToken.None);
                return new Result(true, appt.Id, null);
            }
            catch (DomainException dex)
            {
                return new Result(false, null, dex.Message);
            }
            catch (Exception ex)
            {
                return new Result(false, null, ex.Message);
            }
        }

        var t1 = Task.Run(() => Attempt(1));
        var t2 = Task.Run(() => Attempt(2));
        var results = await Task.WhenAll(t1, t2);
        var successes = results.Count(r => r.Success);
        var failures = results.Count(r => !r.Success);
        Assert.Equal(1, successes);
        Assert.Equal(1, failures);
        // The one that failed should mention a conflict / concurrent slot loss.
        var failed = results.First(r => !r.Success);
        Assert.True(
            failed.Error!.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || failed.Error!.Contains("concurrent", StringComparison.OrdinalIgnoreCase)
            || failed.Error!.Contains("Slot was booked", StringComparison.OrdinalIgnoreCase),
            $"Expected conflict/concurrent-loss error but got '{failed.Error}'.");

        // Sanity: exactly one appointment persisted for the slot.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = db.Appointments.Count(a => a.ClinicianId == seed.ClinicianId
            && a.RoomId == seed.RoomId && a.StartUtc == slotUtc
            && a.Status != AppointmentStatus.Cancelled);
        Assert.Equal(1, count);
    }

    private sealed record Result(bool Success, Guid? AppointmentId, string? Error);
}
