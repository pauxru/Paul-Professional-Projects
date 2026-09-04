using Northstar.Legacy.Web.Legacy;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(20);
    options.Cookie.HttpOnly = true;
});

var app = builder.Build();

DatabaseHelper.InitializeSchema(AppSettings.Get("LegacyDatabase"));
DatabaseHelper.SeedDemoData(AppSettings.Get("LegacyDatabase"));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Claims}/{action=Index}/{id?}");

app.Run();
