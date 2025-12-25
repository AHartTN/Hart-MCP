using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hart.MCP.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMetadataAndOptimizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "atom",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    hilbert_high = table.Column<long>(type: "bigint", nullable: false),
                    hilbert_low = table.Column<long>(type: "bigint", nullable: false),
                    geom = table.Column<Geometry>(type: "geometry(GeometryZM, 0)", nullable: false),
                    is_constant = table.Column<bool>(type: "boolean", nullable: false),
                    seed_value = table.Column<long>(type: "bigint", nullable: true),
                    seed_type = table.Column<int>(type: "integer", nullable: true),
                    refs = table.Column<long[]>(type: "bigint[]", nullable: true),
                    multiplicities = table.Column<int[]>(type: "integer[]", nullable: true),
                    content_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    atom_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_atom", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_atom_atom_type",
                table: "atom",
                column: "atom_type");

            migrationBuilder.CreateIndex(
                name: "IX_atom_content_hash",
                table: "atom",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_atom_created_at",
                table: "atom",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_atom_geom",
                table: "atom",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_atom_hilbert_high_hilbert_low",
                table: "atom",
                columns: new[] { "hilbert_high", "hilbert_low" });

            migrationBuilder.CreateIndex(
                name: "IX_atom_is_constant",
                table: "atom",
                column: "is_constant");

            migrationBuilder.CreateIndex(
                name: "IX_atom_metadata",
                table: "atom",
                column: "metadata")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_atom_refs",
                table: "atom",
                column: "refs")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_atom_seed_value",
                table: "atom",
                column: "seed_value");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "atom");
        }
    }
}
