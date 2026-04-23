using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PositionTradeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // S39：Positions 新增三欄給「交易歷史 + AI 複盤」使用。
            // 三欄皆 nullable — 歷史 row 不補值（歷史交易沒有這些資訊也正常）。
            migrationBuilder.AddColumn<decimal>(
                name: "ExitPrice",
                table: "Positions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StrategyType",
                table: "Positions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParametersSnapshot",
                table: "Positions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ExitPrice", table: "Positions");
            migrationBuilder.DropColumn(name: "StrategyType", table: "Positions");
            migrationBuilder.DropColumn(name: "ParametersSnapshot", table: "Positions");
        }
    }
}
