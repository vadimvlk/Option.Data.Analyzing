using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Option.Data.Database;

public static class DataRegistration
{
    
    public static void RegisterData(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("DefaultConnection")!;
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));
    }
}