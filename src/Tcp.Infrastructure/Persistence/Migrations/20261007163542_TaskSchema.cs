using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tc");

            migrationBuilder.CreateTable(
                name: "LocalIdSequence",
                schema: "tc",
                columns: table => new
                {
                    DefinitionLocalId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Day = table.Column<DateTime>(type: "date", nullable: false),
                    Next = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalIdSequence", x => new { x.DefinitionLocalId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "OperationLog",
                schema: "tc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    At = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskDefinition",
                schema: "tc",
                columns: table => new
                {
                    Urn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LocalId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    NameJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResponsesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomAttributesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TaskDetailsSettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDefinition", x => x.Urn);
                    table.CheckConstraint("CK_TaskDefinition_Json", "ISJSON([NameJson]) = 1 AND ISJSON([ResponsesJson]) = 1 AND ISJSON([ActionsJson]) = 1 AND ISJSON([CustomAttributesJson]) = 1 AND ISJSON([CapabilitiesJson]) = 1");
                });

            migrationBuilder.CreateTable(
                name: "TaskInstance",
                schema: "tc",
                columns: table => new
                {
                    Urn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    LocalId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DefinitionUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false, defaultValue: "MEDIUM"),
                    SubjectJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DescriptionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    CreatedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ModifiedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Processor = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    DueAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    CompletedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskInstance", x => x.Urn)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_TaskInstance_Json", "ISJSON([SubjectJson]) = 1 AND ([DescriptionJson] IS NULL OR ISJSON([DescriptionJson]) = 1)");
                    table.CheckConstraint("CK_TaskInstance_Priority", "[Priority] IN ('VERY_HIGH','HIGH','MEDIUM','LOW')");
                    table.CheckConstraint("CK_TaskInstance_Status", "[Status] IN ('READY','RESERVED','IN_PROGRESS','FOR_RESUBMISSION','INACTIVE','COMPLETED','CANCELED')");
                    table.ForeignKey(
                        name: "FK_TaskInstance_TaskDefinition_DefinitionUrn",
                        column: x => x.DefinitionUrn,
                        principalSchema: "tc",
                        principalTable: "TaskDefinition",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskCustomAttribute",
                schema: "tc",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCustomAttribute", x => new { x.TaskUrn, x.Code });
                    table.ForeignKey(
                        name: "FK_TaskCustomAttribute_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalSchema: "tc",
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskOperationError",
                schema: "tc",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExecutedAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    Code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ExecutedBy = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskOperationError", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskOperationError_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalSchema: "tc",
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecipientGroup",
                schema: "tc",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GroupName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "SQL_Latin1_General_CP1_CI_AS")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecipientGroup", x => new { x.TaskUrn, x.GroupName });
                    table.ForeignKey(
                        name: "FK_TaskRecipientGroup_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalSchema: "tc",
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecipientUser",
                schema: "tc",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false, collation: "Latin1_General_100_BIN2"),
                    GlobalUserId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecipientUser", x => new { x.TaskUrn, x.GlobalUserId });
                    table.ForeignKey(
                        name: "FK_TaskRecipientUser_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalSchema: "tc",
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLog_TaskUrn",
                schema: "tc",
                table: "OperationLog",
                column: "TaskUrn");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLog_UserId",
                schema: "tc",
                table: "OperationLog",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDefinition_LocalId",
                schema: "tc",
                table: "TaskDefinition",
                column: "LocalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_DefinitionUrn",
                schema: "tc",
                table: "TaskInstance",
                column: "DefinitionUrn");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_LocalId",
                schema: "tc",
                table: "TaskInstance",
                column: "LocalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_Processor",
                schema: "tc",
                table: "TaskInstance",
                column: "Processor",
                filter: "[Processor] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_Pull",
                schema: "tc",
                table: "TaskInstance",
                columns: new[] { "ModifiedAt", "Urn" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskOperationError_TaskUrn",
                schema: "tc",
                table: "TaskOperationError",
                column: "TaskUrn");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecipientGroup_GroupName",
                schema: "tc",
                table: "TaskRecipientGroup",
                column: "GroupName");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecipientUser_GlobalUserId",
                schema: "tc",
                table: "TaskRecipientUser",
                column: "GlobalUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalIdSequence",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "OperationLog",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskCustomAttribute",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskOperationError",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskRecipientGroup",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskRecipientUser",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskInstance",
                schema: "tc");

            migrationBuilder.DropTable(
                name: "TaskDefinition",
                schema: "tc");
        }
    }
}
