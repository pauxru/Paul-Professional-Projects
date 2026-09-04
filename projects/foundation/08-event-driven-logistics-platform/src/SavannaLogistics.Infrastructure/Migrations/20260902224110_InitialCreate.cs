using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SavannaLogistics.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Drivers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    LicenceNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PhoneAlias = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drivers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Geofences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Shape = table.Column<int>(type: "INTEGER", nullable: false),
                    CenterLatitude = table.Column<double>(type: "REAL", nullable: true),
                    CenterLongitude = table.Column<double>(type: "REAL", nullable: true),
                    RadiusKm = table.Column<double>(type: "REAL", nullable: true),
                    PolygonJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Geofences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    PolylineJson = table.Column<string>(type: "TEXT", nullable: false),
                    DistanceKm = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vehicles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Registration = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Make = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CapacityKg = table.Column<double>(type: "REAL", nullable: false),
                    CapacityCubicMetres = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AssignedDriverId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vehicles_Drivers_AssignedDriverId",
                        column: x => x.AssignedDriverId,
                        principalTable: "Drivers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RouteStops",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    RadiusKm = table.Column<double>(type: "REAL", nullable: false),
                    DwellMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RouteStops", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RouteStops_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeadLetters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    FailedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadLetters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadLetters_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Trips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentStopSequence = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedStart = table.Column<long>(type: "INTEGER", nullable: false),
                    SlaDueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ActualStart = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ActualCompleted = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastArrivalAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastDepartureAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trips_Routes_RouteId",
                        column: x => x.RouteId,
                        principalTable: "Routes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Trips_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehiclePings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    SpeedKph = table.Column<double>(type: "REAL", nullable: false),
                    HeadingDegrees = table.Column<double>(type: "REAL", nullable: false),
                    OdometerKm = table.Column<double>(type: "REAL", nullable: false),
                    FuelPercent = table.Column<double>(type: "REAL", nullable: false),
                    Ignition = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeviceTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    IngestTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehiclePings_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleStates",
                columns: table => new
                {
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    SpeedKph = table.Column<double>(type: "REAL", nullable: false),
                    HeadingDegrees = table.Column<double>(type: "REAL", nullable: false),
                    OdometerKm = table.Column<double>(type: "REAL", nullable: false),
                    FuelPercent = table.Column<double>(type: "REAL", nullable: false),
                    Ignition = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentTripId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleStates", x => x.VehicleId);
                    table.ForeignKey(
                        name: "FK_VehicleStates_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TripId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 220, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Alerts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EtaPredictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TripId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TargetStopId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalculatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PredictedArrival = table.Column<long>(type: "INTEGER", nullable: false),
                    RemainingDistanceKm = table.Column<double>(type: "REAL", nullable: false),
                    EffectiveSpeedKph = table.Column<double>(type: "REAL", nullable: false),
                    ActualArrival = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AbsoluteErrorSeconds = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EtaPredictions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EtaPredictions_RouteStops_TargetStopId",
                        column: x => x.TargetStopId,
                        principalTable: "RouteStops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EtaPredictions_Trips_TripId",
                        column: x => x.TripId,
                        principalTable: "Trips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EtaPredictions_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LateArrivals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VehicleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SequenceNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LateArrivals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LateArrivals_VehiclePings_PingId",
                        column: x => x.PingId,
                        principalTable: "VehiclePings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LateArrivals_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Fingerprint",
                table: "Alerts",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_TripId",
                table: "Alerts",
                column: "TripId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Type_AcknowledgedAt",
                table: "Alerts",
                columns: new[] { "Type", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_VehicleId_OccurredAt",
                table: "Alerts",
                columns: new[] { "VehicleId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_FailedAt",
                table: "DeadLetters",
                column: "FailedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeadLetters_VehicleId",
                table: "DeadLetters",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Drivers_LicenceNumber",
                table: "Drivers",
                column: "LicenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EtaPredictions_TargetStopId",
                table: "EtaPredictions",
                column: "TargetStopId");

            migrationBuilder.CreateIndex(
                name: "IX_EtaPredictions_TripId_TargetStopId",
                table: "EtaPredictions",
                columns: new[] { "TripId", "TargetStopId" });

            migrationBuilder.CreateIndex(
                name: "IX_EtaPredictions_VehicleId_CalculatedAt",
                table: "EtaPredictions",
                columns: new[] { "VehicleId", "CalculatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Geofences_Name",
                table: "Geofences",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_LateArrivals_PingId",
                table: "LateArrivals",
                column: "PingId");

            migrationBuilder.CreateIndex(
                name: "IX_LateArrivals_VehicleId_SequenceNumber",
                table: "LateArrivals",
                columns: new[] { "VehicleId", "SequenceNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Routes_Name",
                table: "Routes",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_RouteStops_RouteId_Sequence",
                table: "RouteStops",
                columns: new[] { "RouteId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trips_RouteId",
                table: "Trips",
                column: "RouteId");

            migrationBuilder.CreateIndex(
                name: "IX_Trips_VehicleId_Status",
                table: "Trips",
                columns: new[] { "VehicleId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePings_DeviceTimestamp",
                table: "VehiclePings",
                column: "DeviceTimestamp");

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePings_VehicleId_DeviceTimestamp",
                table: "VehiclePings",
                columns: new[] { "VehicleId", "DeviceTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePings_VehicleId_SequenceNumber",
                table: "VehiclePings",
                columns: new[] { "VehicleId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_AssignedDriverId",
                table: "Vehicles",
                column: "AssignedDriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Registration",
                table: "Vehicles",
                column: "Registration",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_Status",
                table: "Vehicles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleStates_LastSeenAt",
                table: "VehicleStates",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleStates_Status",
                table: "VehicleStates",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "DeadLetters");

            migrationBuilder.DropTable(
                name: "EtaPredictions");

            migrationBuilder.DropTable(
                name: "Geofences");

            migrationBuilder.DropTable(
                name: "LateArrivals");

            migrationBuilder.DropTable(
                name: "VehicleStates");

            migrationBuilder.DropTable(
                name: "RouteStops");

            migrationBuilder.DropTable(
                name: "Trips");

            migrationBuilder.DropTable(
                name: "VehiclePings");

            migrationBuilder.DropTable(
                name: "Routes");

            migrationBuilder.DropTable(
                name: "Vehicles");

            migrationBuilder.DropTable(
                name: "Drivers");
        }
    }
}
