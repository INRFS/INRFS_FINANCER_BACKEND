using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace INRFS.Financer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminCollectionMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdminCollectionMonitoring",
                table: "Loans",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminCollectionMonitoring",
                table: "Loans");
        }
    }
}
