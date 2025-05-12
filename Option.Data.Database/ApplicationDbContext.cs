using Option.Data.Shared.Poco;
using Microsoft.EntityFrameworkCore;

namespace Option.Data.Database;

// dotnet ef migrations add Initial --context ApplicationDbContext --project ../Option.Data.Database/ -o "Migrations/ApplicationDb"
// dotnet ef database update --context ApplicationDbContext  --project ../Option.Data.Database/
// dotnet ef migrations list --context ApplicationDbContext --project ../Option.Data.Database/
// dotnet ef migrations remove --context ApplicationDbContext --project ../Option.Data.Database/
public class ApplicationDbContext (DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    
    public virtual DbSet<OptionData> OptionData { get; set; }
    public virtual DbSet<OptionType> OptionType { get; set; }
    public virtual DbSet<CurrencyType> CurrencyType { get; set; }

    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("citext");
        
        modelBuilder.Entity<OptionType>(entity =>
        {
            entity.HasKey(e => e.Id);
        
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(50);
            
            entity.HasData(
                new OptionType { Id = 1, Name = "Call" },
                new OptionType { Id = 2, Name = "Put" }
            );
        });
    
        // Configure CurrencyType entity
        modelBuilder.Entity<CurrencyType>(entity =>
        {
            entity.HasKey(e => e.Id);
        
            entity.Property(e => e.Name)
                .IsRequired()
                .HasMaxLength(50);
            
            entity.HasData(
                new CurrencyType { Id = 1, Name = "BTC" },
                new CurrencyType { Id = 2, Name = "ETH" }
            );
        });

        
        // Configure OptionData entity
        modelBuilder.Entity<OptionData>(entity =>
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
            

            entity.Property(e => e.Strike)
                .IsRequired();
          
            entity.Property(e => e.Iv)
                .HasColumnType("decimal(18, 8)");
            
            entity.Property(e => e.MarkPrice)
                .HasColumnType("decimal(18, 8)");
            
            entity.Property(e => e.Delta)
                .HasColumnType("decimal(18, 8)");
            
            entity.Property(e => e.Gamma)
                .HasColumnType("decimal(18, 8)");

          
    
            entity.HasOne(e => e.Type)
                .WithMany(e => e.Options)
                .HasForeignKey(e => e.OptionTypeId
                )
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);
      
            entity.HasOne(e => e.Currency)
                .WithMany(e => e.Options)
                .HasForeignKey(e => e.CurrencyTypeId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Restrict);

            
            entity.HasIndex(e => new {e.CurrencyTypeId, e.OptionTypeId, e.Strike, e.Expiration });
            entity.HasIndex(e => e.CurrencyTypeId);
            entity.HasIndex(e => e.OptionTypeId);
            entity.HasIndex(e => e.Strike);
            entity.HasIndex(e => e.Expiration);
            
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

    }

    
}