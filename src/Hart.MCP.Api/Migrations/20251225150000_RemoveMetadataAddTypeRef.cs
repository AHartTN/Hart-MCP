using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Hart.MCP.Api.Migrations
{
    /// <summary>
    /// Migration to remove Metadata JSONB and AtomType string columns,
    /// replacing them with TypeRef (atom reference) and Descriptors (atom array).
    ///
    /// ARCHITECTURE CHANGE:
    /// - Metadata was a JSONB blob - now all metadata must be atomized
    /// - AtomType was a string hint - now types are atoms referenced via TypeRef
    /// - Descriptors is an array of atom IDs that describe this atom
    /// </summary>
    public partial class RemoveMetadataAddTypeRef : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop old indexes
            migrationBuilder.DropIndex(
                name: "ix_atom_atom_type",
                table: "atom");

            migrationBuilder.DropIndex(
                name: "ix_atom_metadata",
                table: "atom");

            // Drop old columns
            migrationBuilder.DropColumn(
                name: "atom_type",
                table: "atom");

            migrationBuilder.DropColumn(
                name: "metadata",
                table: "atom");

            // Add new columns
            migrationBuilder.AddColumn<long>(
                name: "type_ref",
                table: "atom",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long[]>(
                name: "descriptors",
                table: "atom",
                type: "bigint[]",
                nullable: true);

            // Create new indexes
            migrationBuilder.CreateIndex(
                name: "ix_atom_type_ref",
                table: "atom",
                column: "type_ref");

            migrationBuilder.Sql(
                "CREATE INDEX ix_atom_descriptors ON atom USING GIN (descriptors)");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop new indexes
            migrationBuilder.DropIndex(
                name: "ix_atom_type_ref",
                table: "atom");

            migrationBuilder.DropIndex(
                name: "ix_atom_descriptors",
                table: "atom");

            // Drop new columns
            migrationBuilder.DropColumn(
                name: "type_ref",
                table: "atom");

            migrationBuilder.DropColumn(
                name: "descriptors",
                table: "atom");

            // Re-add old columns
            migrationBuilder.AddColumn<string>(
                name: "atom_type",
                table: "atom",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                table: "atom",
                type: "jsonb",
                nullable: true);

            // Re-create old indexes
            migrationBuilder.CreateIndex(
                name: "ix_atom_atom_type",
                table: "atom",
                column: "atom_type");

            migrationBuilder.Sql(
                "CREATE INDEX ix_atom_metadata ON atom USING GIN (metadata)");
        }
    }
}
