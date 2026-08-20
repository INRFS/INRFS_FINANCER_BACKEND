using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace INRFS.Financer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileImageDataUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProfileImageDataUrl",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Loans_LoanProductId",
                table: "Loans",
                column: "LoanProductId");

            migrationBuilder.CreateIndex(
                name: "IX_EligibilityChecks_LoanProductId",
                table: "EligibilityChecks",
                column: "LoanProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_EligibilityChecks_LoanProducts_LoanProductId",
                table: "EligibilityChecks",
                column: "LoanProductId",
                principalTable: "LoanProducts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Loans_LoanProducts_LoanProductId",
                table: "Loans",
                column: "LoanProductId",
                principalTable: "LoanProducts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EligibilityChecks_LoanProducts_LoanProductId",
                table: "EligibilityChecks");

            migrationBuilder.DropForeignKey(
                name: "FK_Loans_LoanProducts_LoanProductId",
                table: "Loans");

            migrationBuilder.DropIndex(
                name: "IX_Loans_LoanProductId",
                table: "Loans");

            migrationBuilder.DropIndex(
                name: "IX_EligibilityChecks_LoanProductId",
                table: "EligibilityChecks");

            migrationBuilder.DropColumn(
                name: "ProfileImageDataUrl",
                table: "Users");
        }
    }
}
