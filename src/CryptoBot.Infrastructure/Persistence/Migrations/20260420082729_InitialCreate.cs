using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    FilledQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    StopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    AverageFillPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    PositionSide = table.Column<int>(type: "INTEGER", nullable: false),
                    Commission = table.Column<decimal>(type: "TEXT", nullable: false),
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExchangeOrderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StrategyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Side = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    Leverage = table.Column<int>(type: "INTEGER", nullable: false),
                    MarginMode = table.Column<int>(type: "INTEGER", nullable: false),
                    StopLossPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    TakeProfitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    TrailingStopPercent = table.Column<decimal>(type: "TEXT", nullable: true),
                    TrailingStopPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    StrategyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsClosed = table.Column<bool>(type: "INTEGER", nullable: false),
                    RealizedPnL = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalCommission = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Strategies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StrategyType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Configuration = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StoppedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    TotalTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    WinningTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    LosingTrades = table.Column<int>(type: "INTEGER", nullable: false),
                    CumulativePnL = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxDrawdown = table.Column<decimal>(type: "TEXT", nullable: false),
                    PeakPnL = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Strategies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ExchangeOrderId",
                table: "Orders",
                column: "ExchangeOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StrategyId",
                table: "Orders",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Symbol_Status",
                table: "Orders",
                columns: new[] { "Symbol", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_IsClosed",
                table: "Positions",
                column: "IsClosed");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_StrategyId",
                table: "Positions",
                column: "StrategyId");

            migrationBuilder.CreateIndex(
                name: "IX_Positions_Symbol_IsClosed",
                table: "Positions",
                columns: new[] { "Symbol", "IsClosed" });

            migrationBuilder.CreateIndex(
                name: "IX_Strategies_Name",
                table: "Strategies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Strategies_Status",
                table: "Strategies",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "Strategies");
        }
    }
}
