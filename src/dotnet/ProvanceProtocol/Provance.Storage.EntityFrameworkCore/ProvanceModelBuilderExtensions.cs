using Provance.Storage.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore
{
    /// <summary>Adds the PROVANCE ledger table to an Entity Framework Core model.</summary>
    public static class ProvanceModelBuilderExtensions
    {
        /// <summary>Default table name.</summary>
        public const string DefaultTableName = "provance_ledger";

        /// <summary>
        /// Maps <see cref="LedgerEntryRecord"/>. Call it from <c>OnModelCreating</c> of your own <c>DbContext</c>
        /// and create the table with your migrations, or let <c>UseEntityFrameworkCore(...)</c> use its own context.
        /// </summary>
        /// <remarks>
        /// The unique index on the sequence is required: it is what keeps the chain linear when several
        /// application instances write at the same time. Do not remove it from your migrations.
        /// </remarks>
        public static ModelBuilder AddProvanceLedger(this ModelBuilder modelBuilder, string tableName = DefaultTableName, string? schema = null)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

            modelBuilder.Entity<LedgerEntryRecord>(entity =>
            {
                entity.ToTable(tableName, schema);
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
                entity.Property(e => e.FormatVersion).HasColumnName("format_version");
                entity.Property(e => e.LedgerId).HasColumnName("ledger_id").HasMaxLength(128).IsRequired();
                entity.Property(e => e.Sequence).HasColumnName("seq");
                entity.Property(e => e.SealedAt).HasColumnName("sealed_at");
                entity.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(200).IsRequired();
                entity.Property(e => e.PayloadJson).HasColumnName("payload");
                entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasMaxLength(64).IsRequired();
                entity.Property(e => e.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64).IsRequired();
                entity.Property(e => e.KeyId).HasColumnName("key_id").HasMaxLength(128).IsRequired();
                entity.Property(e => e.Hash).HasColumnName("entry_hash").HasMaxLength(64).IsRequired();
                entity.Property(e => e.Signature).HasColumnName("signature").HasMaxLength(1024).IsRequired();
                entity.Property(e => e.RedactedAt).HasColumnName("redacted_at");
                entity.HasIndex(e => e.Sequence).IsUnique().HasDatabaseName(tableName + "_seq_unique");
            });

            return modelBuilder;
        }
    }
}
