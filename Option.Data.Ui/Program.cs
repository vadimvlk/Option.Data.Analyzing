using System.Text;
using Option.Data.Database;
using Option.Data.Ui.Services;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

Console.OutputEncoding = Encoding.UTF8;
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Debug,
        "[{Timestamp:HH:mm:ss} {SourceContext} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger, true);


//Register PostgresSql.
builder.Services.RegisterData(builder.Configuration);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IOptionsAnalysisHtmlBuilder, OptionsAnalysisHtmlBuilder>();
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();