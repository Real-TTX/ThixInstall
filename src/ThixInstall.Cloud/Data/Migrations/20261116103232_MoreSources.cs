using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThixInstall.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoreSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MacDirectSha256",
                table: "Package",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacDirectUrl",
                table: "Package",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacDirectVersion",
                table: "Package",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacUploadFileName",
                table: "Package",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MacUploadSha256",
                table: "Package",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoreId",
                table: "Package",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StorePackageFamilyName",
                table: "Package",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowsUploadFileName",
                table: "Package",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowsUploadSha256",
                table: "Package",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MacDirectSha256",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "MacDirectUrl",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "MacDirectVersion",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "MacUploadFileName",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "MacUploadSha256",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "StoreId",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "StorePackageFamilyName",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "WindowsUploadFileName",
                table: "Package");

            migrationBuilder.DropColumn(
                name: "WindowsUploadSha256",
                table: "Package");
        }
    }
}
