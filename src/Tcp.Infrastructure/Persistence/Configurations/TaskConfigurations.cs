using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tcp.Domain.Tasks;

namespace Tcp.Infrastructure.Persistence.Configurations;

internal static class TaskColumns
{
    /// <summary>
    /// EF Core 10 infers a case-insensitive comparer for some collations, which would make change tracking treat
    /// URNs that differ only by case as the same key. These columns are binary-collated, so track them ordinally too.
    /// </summary>
    private static readonly ValueComparer<string> Ordinal = new(
        (a, b) => string.Equals(a, b, StringComparison.Ordinal), a => StringComparer.Ordinal.GetHashCode(a), a => a);

    private static PropertyBuilder<T> OrdinalTracking<T>(PropertyBuilder<T> p)
    {
        p.Metadata.SetValueComparer(Ordinal);
        return p;
    }

    /// <summary>URNs and user ids use binary collation so ordering/equality are ordinal (ADR-003, TC-PULL-02).</summary>
    public static PropertyBuilder<string> Urn(this PropertyBuilder<string> p) =>
        OrdinalTracking(p.HasMaxLength(300).IsUnicode(false).UseCollation(Collations.Binary));

    public static PropertyBuilder<T> UserId<T>(this PropertyBuilder<T> p) =>
        OrdinalTracking(p.HasMaxLength(64).IsUnicode(false).UseCollation(Collations.Binary));

    public static PropertyBuilder<string> LocalId(this PropertyBuilder<string> p) =>
        OrdinalTracking(p.HasMaxLength(64).IsUnicode(false).UseCollation(Collations.Binary));
}

internal sealed class TaskDefinitionConfiguration : IEntityTypeConfiguration<TaskDefinitionEntity>
{
    public void Configure(EntityTypeBuilder<TaskDefinitionEntity> b)
    {
        b.ToTable("TaskDefinition", "tc", t =>
        {
            t.HasCheckConstraint("CK_TaskDefinition_Json",
                "ISJSON([NameJson]) = 1 AND ISJSON([ResponsesJson]) = 1 AND ISJSON([ActionsJson]) = 1 " +
                "AND ISJSON([CustomAttributesJson]) = 1 AND ISJSON([CapabilitiesJson]) = 1");
        });
        b.HasKey(d => d.Urn);
        b.Property(d => d.Urn).Urn();
        b.Property(d => d.LocalId).LocalId().IsRequired();
        b.Property(d => d.NameJson).IsRequired();
        b.Property(d => d.ResponsesJson).IsRequired();
        b.Property(d => d.ActionsJson).IsRequired();
        b.Property(d => d.CustomAttributesJson).IsRequired();
        b.Property(d => d.CapabilitiesJson).IsRequired();
        b.Property(d => d.ModifiedAt).HasColumnType("datetime2(3)");
        b.HasIndex(d => d.LocalId).IsUnique();
    }
}

internal sealed class TaskInstanceConfiguration : IEntityTypeConfiguration<TaskInstance>
{
    public void Configure(EntityTypeBuilder<TaskInstance> b)
    {
        var statuses = string.Join(",", TaskStatuses.All.Select(s => $"'{s}'"));
        var priorities = string.Join(",", TaskPriorities.All.Select(p => $"'{p}'"));

        b.ToTable("TaskInstance", "tc", t =>
        {
            t.HasCheckConstraint("CK_TaskInstance_Status", $"[Status] IN ({statuses})");
            t.HasCheckConstraint("CK_TaskInstance_Priority", $"[Priority] IN ({priorities})");
            t.HasCheckConstraint("CK_TaskInstance_Json", "ISJSON([SubjectJson]) = 1 AND ([DescriptionJson] IS NULL OR ISJSON([DescriptionJson]) = 1)");
        });

        // The primary key is nonclustered; the clustered index is the pull order so /tasks is a pure range scan.
        b.HasKey(t => t.Urn).IsClustered(false);
        b.Property(t => t.Urn).Urn();
        b.Property(t => t.LocalId).LocalId().IsRequired();
        b.Property(t => t.DefinitionUrn).Urn();
        b.Property(t => t.Status).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(t => t.Priority).HasMaxLength(16).IsUnicode(false).IsRequired().HasDefaultValue(TaskPriorities.Medium);
        b.Property(t => t.SubjectJson).IsRequired();
        b.Property(t => t.CreatedAt).HasColumnType("datetime2(3)");
        b.Property(t => t.ModifiedAt).HasColumnType("datetime2(3)");
        b.Property(t => t.DueAt).HasColumnType("datetime2(3)");
        b.Property(t => t.CompletedAt).HasColumnType("datetime2(3)");
        b.Property(t => t.CreatedBy).UserId();
        b.Property(t => t.ModifiedBy).UserId();
        b.Property(t => t.Processor).UserId();
        b.Property(t => t.CompletedBy).UserId();
        b.Property(t => t.RowVersion).IsRowVersion();

        b.HasIndex(t => new { t.ModifiedAt, t.Urn }).IsUnique().IsClustered().HasDatabaseName("IX_TaskInstance_Pull");
        b.HasIndex(t => t.LocalId).IsUnique();
        b.HasIndex(t => t.Processor).HasFilter("[Processor] IS NOT NULL").HasDatabaseName("IX_TaskInstance_Processor");
        b.HasIndex(t => t.DefinitionUrn);

        b.HasOne<TaskDefinitionEntity>().WithMany().HasForeignKey(t => t.DefinitionUrn).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TaskRecipientUserConfiguration : IEntityTypeConfiguration<TaskRecipientUser>
{
    public void Configure(EntityTypeBuilder<TaskRecipientUser> b)
    {
        b.ToTable("TaskRecipientUser", "tc");
        b.HasKey(r => new { r.TaskUrn, r.GlobalUserId });
        b.Property(r => r.TaskUrn).Urn();
        b.Property(r => r.GlobalUserId).UserId();
        b.HasIndex(r => r.GlobalUserId);
        b.HasOne<TaskInstance>().WithMany(t => t.RecipientUsers).HasForeignKey(r => r.TaskUrn).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TaskRecipientGroupConfiguration : IEntityTypeConfiguration<TaskRecipientGroup>
{
    public void Configure(EntityTypeBuilder<TaskRecipientGroup> b)
    {
        b.ToTable("TaskRecipientGroup", "tc");
        b.HasKey(r => new { r.TaskUrn, r.GroupName });
        b.Property(r => r.TaskUrn).Urn();
        b.Property(r => r.GroupName).HasMaxLength(256).UseCollation(Collations.CaseInsensitive);
        b.HasIndex(r => r.GroupName);
        b.HasOne<TaskInstance>().WithMany(t => t.RecipientGroups).HasForeignKey(r => r.TaskUrn).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TaskCustomAttributeConfiguration : IEntityTypeConfiguration<TaskCustomAttribute>
{
    public void Configure(EntityTypeBuilder<TaskCustomAttribute> b)
    {
        b.ToTable("TaskCustomAttribute", "tc");
        b.HasKey(a => new { a.TaskUrn, a.Code });
        b.Property(a => a.TaskUrn).Urn();
        b.Property(a => a.Code).HasMaxLength(64).IsUnicode(false);
        b.Property(a => a.Value).HasMaxLength(255).IsRequired();
        b.HasOne<TaskInstance>().WithMany(t => t.CustomAttributes).HasForeignKey(a => a.TaskUrn).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TaskOperationErrorConfiguration : IEntityTypeConfiguration<TaskOperationError>
{
    public void Configure(EntityTypeBuilder<TaskOperationError> b)
    {
        b.ToTable("TaskOperationError", "tc");
        b.HasKey(e => e.Id);
        b.Property(e => e.TaskUrn).Urn();
        b.Property(e => e.ExecutedAt).HasColumnType("datetime2(3)");
        b.Property(e => e.Code).HasMaxLength(64).IsUnicode(false).IsRequired();
        b.Property(e => e.Message).HasMaxLength(2000).IsRequired();
        b.Property(e => e.ExecutedBy).UserId();
        b.HasIndex(e => e.TaskUrn);
        b.HasOne<TaskInstance>().WithMany(t => t.OperationErrors).HasForeignKey(e => e.TaskUrn).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OperationLogConfiguration : IEntityTypeConfiguration<OperationLogEntry>
{
    public void Configure(EntityTypeBuilder<OperationLogEntry> b)
    {
        b.ToTable("OperationLog", "tc");
        b.HasKey(e => e.Id);
        b.Property(e => e.TaskUrn).Urn();
        b.Property(e => e.Kind).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(e => e.Code).HasMaxLength(64).IsRequired();
        b.Property(e => e.Comment).HasMaxLength(2000);
        b.Property(e => e.ReasonCode).HasMaxLength(64);
        b.Property(e => e.UserId).UserId();
        b.Property(e => e.At).HasColumnType("datetime2(3)");
        b.Property(e => e.Outcome).HasMaxLength(16).IsUnicode(false).IsRequired();
        b.Property(e => e.ErrorCode).HasMaxLength(64).IsUnicode(false);
        b.HasIndex(e => e.TaskUrn);
        b.HasIndex(e => e.UserId);
    }
}

internal sealed class LocalIdSequenceConfiguration : IEntityTypeConfiguration<LocalIdSequence>
{
    public void Configure(EntityTypeBuilder<LocalIdSequence> b)
    {
        b.ToTable("LocalIdSequence", "tc");
        b.HasKey(s => new { s.DefinitionLocalId, s.Day });
        b.Property(s => s.DefinitionLocalId).HasMaxLength(64).IsUnicode(false);
        b.Property(s => s.Day).HasColumnType("date");
    }
}
