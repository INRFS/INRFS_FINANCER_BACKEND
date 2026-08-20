using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace INRFS.Financer.Infrastructure.Migrations;

[DbContext(typeof(FinancerDbContext))]
[Migration("20260819093000_AddCollectionFollowUpDate")]
public sealed class AddCollectionFollowUpDate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<DateOnly>(
        name: "NextFollowUpDate", table: "CollectionCases", type: "date", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "NextFollowUpDate", table: "CollectionCases");
}
