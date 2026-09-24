using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSmtpSettingsToStoreSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmtpFrom",
                table: "StoreSettings",
                type: "nvarchar(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "StoreSettings",
                type: "varchar(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "StoreSettings",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "StoreSettings",
                type: "int",
                nullable: false,
                defaultValue: 587);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUser",
                table: "StoreSettings",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmtpFrom",
                table: "StoreSettings");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "StoreSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "StoreSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "StoreSettings");

            migrationBuilder.DropColumn(
                name: "SmtpUser",
                table: "StoreSettings");
        }
    }
}
