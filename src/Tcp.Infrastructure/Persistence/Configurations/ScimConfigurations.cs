using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tcp.Domain.Identity;

namespace Tcp.Infrastructure.Persistence.Configurations;

internal static class Collations
{
    /// <summary>Case-sensitive, ordinal: identifiers that must compare exactly.</summary>
    public const string Binary = "Latin1_General_100_BIN2";

    /// <summary>Case-insensitive: user names, display names, e-mail (SCIM string comparison).</summary>
    public const string CaseInsensitive = "SQL_Latin1_General_CP1_CI_AS";
}

internal sealed class ScimUserConfiguration : IEntityTypeConfiguration<ScimUser>
{
    public void Configure(EntityTypeBuilder<ScimUser> b)
    {
        b.ToTable("ScimUser", "idm", t =>
        {
            t.HasCheckConstraint("CK_ScimUser_EmailsJson", "ISJSON([EmailsJson]) = 1");
            t.HasCheckConstraint("CK_ScimUser_RawJson", "ISJSON([RawJson]) = 1");
        });
        b.HasKey(u => u.Id);
        b.Property(u => u.Id).ValueGeneratedNever();
        b.Property(u => u.UserName).HasMaxLength(256).UseCollation(Collations.CaseInsensitive).IsRequired();
        b.Property(u => u.ExternalId).HasMaxLength(256).UseCollation(Collations.Binary);
        b.Property(u => u.GlobalUserId).HasMaxLength(64).IsUnicode(false).UseCollation(Collations.Binary);
        b.Property(u => u.DisplayName).HasMaxLength(256).UseCollation(Collations.CaseInsensitive);
        b.Property(u => u.GivenName).HasMaxLength(128).UseCollation(Collations.CaseInsensitive);
        b.Property(u => u.FamilyName).HasMaxLength(128).UseCollation(Collations.CaseInsensitive);
        b.Property(u => u.PrimaryEmail).HasMaxLength(320).UseCollation(Collations.CaseInsensitive);
        b.Property(u => u.EmailsJson).IsRequired();
        b.Property(u => u.EmailsSearch).HasMaxLength(2000).UseCollation(Collations.CaseInsensitive).IsRequired();
        b.Property(u => u.RawJson).IsRequired();
        b.Property(u => u.Active).HasDefaultValue(true);
        b.Property(u => u.Created).HasColumnType("datetime2(3)");
        b.Property(u => u.LastModified).HasColumnType("datetime2(3)");

        b.HasIndex(u => u.UserName).IsUnique();
        b.HasIndex(u => u.GlobalUserId).IsUnique().HasFilter("[GlobalUserId] IS NOT NULL");
        b.HasIndex(u => u.PrimaryEmail);
        b.HasIndex(u => u.ExternalId);
    }
}

internal sealed class ScimGroupConfiguration : IEntityTypeConfiguration<ScimGroup>
{
    public void Configure(EntityTypeBuilder<ScimGroup> b)
    {
        b.ToTable("ScimGroup", "idm");
        b.HasKey(g => g.Id);
        b.Property(g => g.Id).ValueGeneratedNever();
        b.Property(g => g.DisplayName).HasMaxLength(256).UseCollation(Collations.CaseInsensitive).IsRequired();
        b.Property(g => g.ExternalId).HasMaxLength(256).UseCollation(Collations.Binary);
        b.Property(g => g.Created).HasColumnType("datetime2(3)");
        b.Property(g => g.LastModified).HasColumnType("datetime2(3)");
        b.HasIndex(g => g.DisplayName).IsUnique();
    }
}

internal sealed class ScimGroupMemberConfiguration : IEntityTypeConfiguration<ScimGroupMember>
{
    public void Configure(EntityTypeBuilder<ScimGroupMember> b)
    {
        b.ToTable("ScimGroupMember", "idm");
        b.HasKey(m => new { m.GroupId, m.UserId });
        b.HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(m => m.UserId);
    }
}

internal sealed class ScimAuditConfiguration : IEntityTypeConfiguration<ScimAuditEntry>
{
    public void Configure(EntityTypeBuilder<ScimAuditEntry> b)
    {
        b.ToTable("ScimAudit", "idm");
        b.HasKey(a => a.Id);
        b.Property(a => a.At).HasColumnType("datetime2(3)");
        b.Property(a => a.ClientId).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(a => a.Operation).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(a => a.ResourceType).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(a => a.Summary).HasMaxLength(400).IsRequired();
        b.HasIndex(a => a.At);
    }
}
