import "../../test/setupDom.ts";
import type { IPromptTransport } from "./promptApi.ts";
const { promptApi } = await import("./promptApi.ts");

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message);
}

async function run(): Promise<void> {
  const calls: Array<{ command: string; payload: unknown }> = [];
  const template = { templateId: "template-script", purpose: "Script", name: "Kịch bản", isActive: true, createdAtUtc: "2026-10-28T00:00:00Z", updatedAtUtc: "2026-10-28T00:00:00Z" };
  const content = { templateId: template.templateId, templateText: "Viết về {topic}", variableDefinitionsJson: "[]", updatedAtUtc: template.updatedAtUtc };
  const transport: IPromptTransport = {
    invoke: async <TData>(command: string, payload: unknown): Promise<TData> => {
      calls.push({ command, payload });
      const response = command === "prompt.list_templates"
        ? { templates: [template] }
        : command === "prompt.get_template"
          ? { template, content }
          : command === "prompt.save_template"
            ? { template, content }
            : command === "prompt.activate_template" ? template : { renderedText: "Viết về AI", templateId: template.templateId, variableCount: 1 };
      return response as TData;
    },
  };

  assert((await promptApi.listTemplates(transport))[0]?.templateId === template.templateId, "List must return the global template.");
  const loaded = await promptApi.getTemplate({ purpose: "Script" }, transport);
  assert(loaded.content.templateText === content.templateText, "Get must return current template content.");
  const saved = await promptApi.saveTemplate({ purpose: "Script", name: "Kịch bản", templateId: null, templateText: content.templateText }, transport);
  assert(saved.template.templateId === template.templateId, "Save must return the stored template.");
  assert((await promptApi.activateTemplate(template.templateId, transport)).isActive, "Activate must return the selected template.");
  const preview = await promptApi.preview({ templateId: template.templateId, variables: { topic: "AI" } }, transport);
  assert(preview.renderedText === "Viết về AI", "Preview must return rendered text.");
  assert(calls.map((call) => call.command).join(",") === "prompt.list_templates,prompt.get_template,prompt.save_template,prompt.activate_template,prompt.preview", "Prompt bridge should expose current commands.");
}

void run();
