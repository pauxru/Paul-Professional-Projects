# Northstar Legacy Claims — As-Is Simulation

> **Important:** This is an honest simulation of a classic 2010-era ASP.NET enterprise application. The host cannot install .NET Framework 4.x, so the app targets `net10.0` solely to compile and run on this workstation. Its intentionally unsafe architecture, static configuration, raw ADO.NET, synchronous blocking calls, fat controller, session workflow, and filesystem storage model represent the legacy *style*, not recommended modern .NET practice.

Run with `dotnet run --project legacy\Northstar.Legacy.Web --launch-profile http` and browse `http://localhost:5102`. It is deliberately isolated from the modern target and exists to support characterization, security, and migration work.
