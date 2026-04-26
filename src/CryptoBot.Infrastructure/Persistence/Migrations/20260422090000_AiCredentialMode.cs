using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiCredentialMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // S30-FIX2：為 AiCredentials 加入 Mode 欄位（0 = Eco / 1 = Pro）。
            // 既存 row 以 defaultValue: 0 補為 Eco，避免升級後 Provider 讀到 null 時爆炸。
            migrationBuilder.AddColumn<int>(
                name: "Mode",
                table: "AiCredentials",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Mode",
                table: "AiCredentials");
        }
    }
}
