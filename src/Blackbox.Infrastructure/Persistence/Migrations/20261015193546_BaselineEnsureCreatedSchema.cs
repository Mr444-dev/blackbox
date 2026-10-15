using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Blackbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BaselineEnsureCreatedSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dead_letter_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    exception_message = table.Column<string>(type: "text", nullable: false),
                    stack_trace = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    replayed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dead_letter_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    message_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    topic = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resource_reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workflow_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_reservations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "resources",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    available_quantity = table.Column<int>(type: "integer", nullable: false),
                    allocated_quantity = table.Column<int>(type: "integer", nullable: false),
                    consumed_quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resources", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    error_details = table.Column<string>(type: "jsonb", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inbox_messages_consumer_name_message_id",
                table: "inbox_messages",
                columns: new[] { "consumer_name", "message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_resource_reservations_resource_id_workflow_id",
                table: "resource_reservations",
                columns: new[] { "resource_id", "workflow_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflows_correlation_id",
                table: "workflows",
                column: "correlation_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dead_letter_messages");

            migrationBuilder.DropTable(
                name: "inbox_messages");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "resource_reservations");

            migrationBuilder.DropTable(
                name: "resources");

            migrationBuilder.DropTable(
                name: "workflows");
        }
    }
}
