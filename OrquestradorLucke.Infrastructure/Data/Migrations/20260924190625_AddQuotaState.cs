using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrquestradorLucke.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotaState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quota_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quota_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_quota_states_provider_name",
                table: "quota_states",
                column: "provider_name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quota_states");
        }
    }
}
