using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BacktestKlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HistoricalKlines",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CloseTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Open = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false),
                    High = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false),
                    Low = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false),
                    Volume = table.Column<decimal>(type: "TEXT", precision: 28, scale: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalKlines", x => new { x.Symbol, x.Interval, x.OpenTime });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HistoricalKlines");
        }
    }
}
