using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThixInstall.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstallRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MacRoute",
                table: "Package",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowsRoute",
                table: "Package",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MacRoute",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "WindowsRoute",
                table: "Package");
        }
    }
}
