using Microsoft.EntityFrameworkCore;

namespace Option.Data.Database;

// dotnet ef migrations add Initial --context ApplicationDbContext --project ../Option.Data.Database/ -o "Migrations/ApplicationDb"
// dotnet ef database update --context ApplicationDbContext  --project ../Option.Data.Database/
// dotnet ef migrations list --context ApplicationDbContext --project ../Option.Data.Database/
// dotnet ef migrations remove --context ApplicationDbContext --project ../Option.Data.Database/
public class ApplicationDbContext (DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    
    public virtual DbSet<Shared.Poco.OptionData> OptionData { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("citext");
        
        // Configure OptionData entity
        modelBuilder.Entity<Shared.Poco.OptionData>(entity =>
        {
            entity.HasKey(a => a.Id);

            entity.Property(a => a.Id)
                .UseIdentityAlwaysColumn()
                .HasIdentityOptions(startValue: 1);

            entity.Property(e => e.InstrumentName)
                .IsRequired()
                .HasMaxLength(100);
          
            entity.Property(e => e.Expiration)
                .IsRequired()
                .HasMaxLength(50);
            
            entity.Property(e => e.CallOi)
                .HasColumnType("decimal(18, 8)");

            entity.Property(e => e.Strike)
                .IsRequired();
          
            entity.Property(e => e.Iv)
                .HasColumnType("decimal(18, 8)");
          
            entity.Property(e => e.PutOi)
                .HasColumnType("decimal(18, 8)");
          
            entity.Property(e => e.CallPrice)
                .HasColumnType("decimal(18, 8)");
          
            entity.Property(e => e.PutPrice)
                .HasColumnType("decimal(18, 8)");
    
            // Configure enum properties
            entity.Property(e => e.Type)
                .IsRequired()
                .HasConversion<string>();
          
            entity.Property(e => e.Currency)
                .IsRequired()
                .HasConversion<string>();
            
            entity.HasIndex(e => new {e.Currency, e.Type, e.Strike, e.Expiration });
            entity.HasIndex(e => e.Currency);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.Strike);
            entity.HasIndex(e => e.Expiration);
            
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

    }

    
}