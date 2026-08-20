using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace INRFS.Financer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInterestOnlyLoanSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterestDays",
                table: "PaymentSchedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PeriodEnd",
                table: "PaymentSchedules",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<DateOnly>(
                name: "PeriodStart",
                table: "PaymentSchedules",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<int>(
                name: "DurationUnit",
                table: "Loans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DurationValue",
                table: "Loans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InterestCollectionFrequency",
                table: "Loans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "InterestRate",
                table: "Loans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "InterestRateBasis",
                table: "Loans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "Loans"
                SET "DurationValue" = "TenureMonths", "DurationUnit" = 2,
                    "InterestRate" = "AnnualInterestRate", "InterestRateBasis" = 0,
                    "InterestCollectionFrequency" = 2;
                UPDATE "PaymentSchedules"
                SET "PeriodEnd" = "DueDate",
                    "PeriodStart" = ("DueDate" - INTERVAL '1 month')::date,
                    "InterestDays" = "DueDate" - (("DueDate" - INTERVAL '1 month')::date);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterestDays",
                table: "PaymentSchedules");

            migrationBuilder.DropColumn(
                name: "PeriodEnd",
                table: "PaymentSchedules");

            migrationBuilder.DropColumn(
                name: "PeriodStart",
                table: "PaymentSchedules");

            migrationBuilder.DropColumn(
                name: "DurationUnit",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "DurationValue",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "InterestCollectionFrequency",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "InterestRate",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "InterestRateBasis",
                table: "Loans");
        }
    }
}
