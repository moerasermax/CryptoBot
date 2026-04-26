using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StrategyOptimizationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StrategyOptimizationSettings",
                columns: table => new
                {
                    StrategyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    Score = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrategyOptimizationSettings", x => new { x.StrategyId, x.Symbol, x.Interval });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StrategyOptimizationSettings");
        }
    }
}
