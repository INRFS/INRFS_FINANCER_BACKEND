using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace INRFS.Financer.Infrastructure.Migrations;

[DbContext(typeof(FinancerDbContext))]
[Migration("20260817090000_AllowSupplementaryServiceChargeInvoices")]
public partial class AllowSupplementaryServiceChargeInvoices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ServiceChargeInvoices_FinancerId_PeriodStart_PeriodEnd",
            table: "ServiceChargeInvoices");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceChargeInvoices_FinancerId_PeriodStart_PeriodEnd",
            table: "ServiceChargeInvoices",
            columns: new[] { "FinancerId", "PeriodStart", "PeriodEnd" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ServiceChargeInvoices_FinancerId_PeriodStart_PeriodEnd",
            table: "ServiceChargeInvoices");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceChargeInvoices_FinancerId_PeriodStart_PeriodEnd",
            table: "ServiceChargeInvoices",
            columns: new[] { "FinancerId", "PeriodStart", "PeriodEnd" },
            unique: true);
    }
}
