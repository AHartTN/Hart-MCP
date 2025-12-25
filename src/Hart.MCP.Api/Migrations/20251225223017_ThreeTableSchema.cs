using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Hart.MCP.Api.Migrations
{
    /// <inheritdoc />
    public partial class ThreeTableSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "atom");

            migrationBuilder.CreateTable(
                name: "composition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    content_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    hilbert_high = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    hilbert_low = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    geom = table.Column<Geometry>(type: "geometry(GeometryZM, 0)", nullable: true),
                    type_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_composition", x => x.id);
                    table.ForeignKey(
                        name: "FK_composition_composition_type_id",
                        column: x => x.type_id,
                        principalTable: "composition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "constant",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    seed_value = table.Column<long>(type: "bigint", nullable: false),
                    seed_type = table.Column<int>(type: "integer", nullable: false),
                    content_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    hilbert_high = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    hilbert_low = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    geom = table.Column<Geometry>(type: "geometry(PointZM, 0)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_constant", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "relation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    composition_id = table.Column<long>(type: "bigint", nullable: false),
                    child_constant_id = table.Column<long>(type: "bigint", nullable: true),
                    child_composition_id = table.Column<long>(type: "bigint", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    multiplicity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relation", x => x.id);
                    table.ForeignKey(
                        name: "FK_relation_composition_child_composition_id",
                        column: x => x.child_composition_id,
                        principalTable: "composition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_relation_composition_composition_id",
                        column: x => x.composition_id,
                        principalTable: "composition",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_relation_constant_child_constant_id",
                        column: x => x.child_constant_id,
                        principalTable: "constant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_composition_content_hash",
                table: "composition",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_composition_geom",
                table: "composition",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_composition_hilbert_high_hilbert_low",
                table: "composition",
                columns: new[] { "hilbert_high", "hilbert_low" });

            migrationBuilder.CreateIndex(
                name: "IX_composition_type_id",
                table: "composition",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "IX_constant_content_hash",
                table: "constant",
                column: "content_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_constant_geom",
                table: "constant",
                column: "geom")
                .Annotation("Npgsql:IndexMethod", "GIST");

            migrationBuilder.CreateIndex(
                name: "IX_constant_hilbert_high_hilbert_low",
                table: "constant",
                columns: new[] { "hilbert_high", "hilbert_low" });

            migrationBuilder.CreateIndex(
                name: "IX_constant_seed_type_seed_value",
                table: "constant",
                columns: new[] { "seed_type", "seed_value" });

            migrationBuilder.CreateIndex(
                name: "IX_relation_child_composition_id",
                table: "relation",
                column: "child_composition_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_child_constant_id",
                table: "relation",
                column: "child_constant_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_composition_id",
                table: "relation",
                column: "composition_id");

            migrationBuilder.CreateIndex(
                name: "IX_relation_composition_id_position",
                table: "relation",
                columns: new[] { "composition_id", "position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relation");

            migrationBuilder.DropTable(
                name: "composition");

            migrationBuilder.DropTable(
                name: "constant");

            migrationBuilder.CreateTable(
                name: "atom",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    atom_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    content_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    geom = table.Column<Geometry>(type: "geometry(GeometryZM, 0)", nullable: false),
                    hilbert_high = table.Column<long>(type: "bigint", nullable: false),
                    hilbert_low = table.Column<long>(type: "bigint", nullable: false),
                    is_constant = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    multiplicities = table.Column<int[]>(type: "integer[]", nullable: true),
                    refs = table.Column<long[]>(type: "bigint[]", nullable: true),
                    seed_type = table.Column<int>(type: "integer", nullable: true),
                    seed_value = table.Column<long>(type: "bigint", nullable: true)
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
    }
}
