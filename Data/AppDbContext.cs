using Microsoft.EntityFrameworkCore;
using QwenSolvingApi.Models;

namespace QwenSolvingApi.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<CaptchaUrl> CaptchaUrls => Set<CaptchaUrl>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired().HasMaxLength(128);
            entity.HasIndex(e => e.Username).IsUnique();
            entity.Property(e => e.PasswordHash).IsRequired();
            entity.Property(e => e.Role).IsRequired().HasMaxLength(32).HasDefaultValue("Admin");
            entity.Property(e => e.CreatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<CaptchaUrl>(entity =>
        {
            entity.ToTable("CaptchaUrls");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Url).IsRequired().HasMaxLength(2048);
            entity.HasIndex(e => e.Url).IsUnique();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
        });
    }
}
