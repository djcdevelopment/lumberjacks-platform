using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Game.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NatureTwoPointZero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "natural_resources",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    position_x = table.Column<double>(type: "double precision", nullable: false),
                    position_y = table.Column<double>(type: "double precision", nullable: false),
                    position_z = table.Column<double>(type: "double precision", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: false),
                    health = table.Column<double>(type: "double precision", nullable: false),
                    stump_health = table.Column<double>(type: "double precision", nullable: false),
                    regrowth_progress = table.Column<double>(type: "double precision", nullable: false),
                    lean_x = table.Column<double>(type: "double precision", nullable: false),
                    lean_z = table.Column<double>(type: "double precision", nullable: false),
                    growth_history = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_natural_resources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "region_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: false),
                    altitude_grid = table.Column<string>(type: "jsonb", nullable: false),
                    humidity_grid = table.Column<string>(type: "jsonb", nullable: false),
                    grid_width = table.Column<int>(type: "integer", nullable: false),
                    grid_height = table.Column<int>(type: "integer", nullable: false),
                    trade_wind_x = table.Column<double>(type: "double precision", nullable: false),
                    trade_wind_z = table.Column<double>(type: "double precision", nullable: false),
                    geologic_history = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_region_profiles", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_natural_resources_region_id",
                table: "natural_resources",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "IX_region_profiles_region_id",
                table: "region_profiles",
                column: "region_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "natural_resources");

            migrationBuilder.DropTable(
                name: "region_profiles");
        }
    }
}
