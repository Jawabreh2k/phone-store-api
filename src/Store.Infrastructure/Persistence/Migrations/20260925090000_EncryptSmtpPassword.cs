using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Store.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EncryptSmtpPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The column now holds a Data Protection ciphertext (same shape as OutboxMessages.ProtectedSecret), not
            // the raw password, so any previously saved plaintext value can no longer be decrypted; clear it rather
            // than leave a value that would fail to authenticate.
            migrationBuilder.Sql("UPDATE [StoreSettings] SET [SmtpPassword] = NULL WHERE [SmtpPassword] IS NOT NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "SmtpPassword",
                table: "StoreSettings",
                type: "varchar(max)",
                unicode: false,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(255)",
                oldMaxLength: 255,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [StoreSettings] SET [SmtpPassword] = NULL WHERE [SmtpPassword] IS NOT NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "SmtpPassword",
                table: "StoreSettings",
                type: "nvarchar(255)",
                maxLength: 255,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(max)",
                oldUnicode: false,
                oldNullable: true);
        }
    }
}
