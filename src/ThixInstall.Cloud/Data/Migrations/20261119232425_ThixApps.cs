using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThixInstall.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ThixApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstallerOptions",
                table: "Package",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThixManifest",
                table: "Package",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThixRepository",
                table: "Package",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstallChoices",
                table: "BundleItem",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstallerOptions",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "ThixManifest",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "ThixRepository",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "InstallChoices",
                table: "BundleItem");
        }
    }
}
