using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrquestradorLucke.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "agent_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    complexidade = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    atualizado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    pull_request_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_tasks_status_criado_em",
                table: "agent_tasks",
                columns: new[] { "status", "criado_em" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_tasks");
        }
    }
}
