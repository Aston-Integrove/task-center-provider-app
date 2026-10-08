using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalIdSequence",
                columns: table => new
                {
                    DefinitionLocalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Next = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalIdSequence", x => new { x.DefinitionLocalId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "OperationLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    Kind = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ReasonCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false, collation: "BINARY"),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimAudit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimAudit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimGroup",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "BINARY"),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScimUser",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "BINARY"),
                    GlobalUserId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true, collation: "BINARY"),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "NOCASE"),
                    GivenName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, collation: "NOCASE"),
                    FamilyName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, collation: "NOCASE"),
                    PrimaryEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true, collation: "NOCASE"),
                    EmailsJson = table.Column<string>(type: "TEXT", nullable: false),
                    EmailsSearch = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false, collation: "NOCASE"),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    Created = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModified = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimUser", x => x.Id);
                    table.CheckConstraint("CK_ScimUser_EmailsJson", "json_valid(\"EmailsJson\")");
                    table.CheckConstraint("CK_ScimUser_RawJson", "json_valid(\"RawJson\")");
                });

            migrationBuilder.CreateTable(
                name: "TaskDefinition",
                columns: table => new
                {
                    Urn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    LocalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false, collation: "BINARY"),
                    NameJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResponsesJson = table.Column<string>(type: "TEXT", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CustomAttributesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false),
                    TaskDetailsSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDefinition", x => x.Urn);
                    table.CheckConstraint("CK_TaskDefinition_Json", "json_valid(\"NameJson\") AND json_valid(\"ResponsesJson\") AND json_valid(\"ActionsJson\") AND json_valid(\"CustomAttributesJson\") AND json_valid(\"CapabilitiesJson\")");
                });

            migrationBuilder.CreateTable(
                name: "ScimGroupMember",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScimGroupMember", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ScimGroupMember_ScimGroup_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ScimGroup",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScimGroupMember_ScimUser_UserId",
                        column: x => x.UserId,
                        principalTable: "ScimUser",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TaskInstance",
                columns: table => new
                {
                    Urn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    LocalId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false, collation: "BINARY"),
                    DefinitionUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    Status = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "TEXT", unicode: false, maxLength: 16, nullable: false, defaultValue: "MEDIUM"),
                    SubjectJson = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true, collation: "BINARY"),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedBy = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true, collation: "BINARY"),
                    Processor = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true, collation: "BINARY"),
                    DueAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: true, collation: "BINARY"),
                    RowVersion = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskInstance", x => x.Urn);
                    table.CheckConstraint("CK_TaskInstance_Json", "json_valid(\"SubjectJson\") AND (\"DescriptionJson\" IS NULL OR json_valid(\"DescriptionJson\"))");
                    table.CheckConstraint("CK_TaskInstance_Priority", "\"Priority\" IN ('VERY_HIGH','HIGH','MEDIUM','LOW')");
                    table.CheckConstraint("CK_TaskInstance_Status", "\"Status\" IN ('READY','RESERVED','IN_PROGRESS','FOR_RESUBMISSION','INACTIVE','COMPLETED','CANCELED')");
                    table.ForeignKey(
                        name: "FK_TaskInstance_TaskDefinition_DefinitionUrn",
                        column: x => x.DefinitionUrn,
                        principalTable: "TaskDefinition",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskCustomAttribute",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    Code = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskCustomAttribute", x => new { x.TaskUrn, x.Code });
                    table.ForeignKey(
                        name: "FK_TaskCustomAttribute_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskOperationError",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    ExecutedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ExecutedBy = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false, collation: "BINARY")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskOperationError", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskOperationError_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecipientGroup",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecipientGroup", x => new { x.TaskUrn, x.GroupName });
                    table.ForeignKey(
                        name: "FK_TaskRecipientGroup_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRecipientUser",
                columns: table => new
                {
                    TaskUrn = table.Column<string>(type: "TEXT", unicode: false, maxLength: 300, nullable: false, collation: "BINARY"),
                    GlobalUserId = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false, collation: "BINARY")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRecipientUser", x => new { x.TaskUrn, x.GlobalUserId });
                    table.ForeignKey(
                        name: "FK_TaskRecipientUser_TaskInstance_TaskUrn",
                        column: x => x.TaskUrn,
                        principalTable: "TaskInstance",
                        principalColumn: "Urn",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationLog_TaskUrn",
                table: "OperationLog",
                column: "TaskUrn");

            migrationBuilder.CreateIndex(
                name: "IX_OperationLog_UserId",
                table: "OperationLog",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimAudit_At",
                table: "ScimAudit",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroup_DisplayName",
                table: "ScimGroup",
                column: "DisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScimGroupMember_UserId",
                table: "ScimGroupMember",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_ExternalId",
                table: "ScimUser",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_GlobalUserId",
                table: "ScimUser",
                column: "GlobalUserId",
                unique: true,
                filter: "\"GlobalUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_PrimaryEmail",
                table: "ScimUser",
                column: "PrimaryEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ScimUser_UserName",
                table: "ScimUser",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDefinition_LocalId",
                table: "TaskDefinition",
                column: "LocalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_DefinitionUrn",
                table: "TaskInstance",
                column: "DefinitionUrn");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_LocalId",
                table: "TaskInstance",
                column: "LocalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_Processor",
                table: "TaskInstance",
                column: "Processor",
                filter: "\"Processor\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskInstance_Pull",
                table: "TaskInstance",
                columns: new[] { "ModifiedAt", "Urn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskOperationError_TaskUrn",
                table: "TaskOperationError",
                column: "TaskUrn");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecipientGroup_GroupName",
                table: "TaskRecipientGroup",
                column: "GroupName");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRecipientUser_GlobalUserId",
                table: "TaskRecipientUser",
                column: "GlobalUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalIdSequence");

            migrationBuilder.DropTable(
                name: "OperationLog");

            migrationBuilder.DropTable(
                name: "ScimAudit");

            migrationBuilder.DropTable(
                name: "ScimGroupMember");

            migrationBuilder.DropTable(
                name: "TaskCustomAttribute");

            migrationBuilder.DropTable(
                name: "TaskOperationError");

            migrationBuilder.DropTable(
                name: "TaskRecipientGroup");

            migrationBuilder.DropTable(
                name: "TaskRecipientUser");

            migrationBuilder.DropTable(
                name: "ScimGroup");

            migrationBuilder.DropTable(
                name: "ScimUser");

            migrationBuilder.DropTable(
                name: "TaskInstance");

            migrationBuilder.DropTable(
                name: "TaskDefinition");
        }
    }
}
