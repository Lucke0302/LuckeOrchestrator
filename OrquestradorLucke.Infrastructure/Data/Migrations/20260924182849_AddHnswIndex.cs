using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrquestradorLucke.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHnswIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_code_documents_embedding_hnsw",
                table: "code_documents",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_code_documents_embedding_hnsw",
                table: "code_documents");
        }
    }
}
