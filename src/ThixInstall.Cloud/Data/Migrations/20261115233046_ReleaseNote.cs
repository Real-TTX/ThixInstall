using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ThixInstall.Cloud.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "PackageRelease",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "PackageRelease");
        }
    }
}
