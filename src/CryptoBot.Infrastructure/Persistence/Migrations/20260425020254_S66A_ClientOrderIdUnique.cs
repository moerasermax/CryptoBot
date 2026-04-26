using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class S66A_ClientOrderIdUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId",
                unique: true,
                filter: "\"ClientOrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId");
        }
    }
}
