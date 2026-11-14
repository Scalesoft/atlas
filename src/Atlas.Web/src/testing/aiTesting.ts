// Test helper for the AI administration pages: answers `/api/admin/ai/...` from the in-memory mock.
import { createAiAdminMock, type AiAdminMock, type AiAdminMockOptions } from "../components/settings/general/ai/aiAdminMock";
import { fakeApi, type FakeCall, type FakeHandler } from "../components/settings/general/testing";

export function useAiMock(options: AiAdminMockOptions = {}, extra?: FakeHandler): { mock: AiAdminMock; calls: FakeCall[] } {
  const mock = createAiAdminMock(options);
  const calls = fakeApi((method, path, body) => {
    const custom = extra?.(method, path, body);
    if (custom !== undefined) return custom;
    const result = mock.handle(method, path, body);
    if (result.status >= 400) return new Response(JSON.stringify(result.body), { status: result.status });
    return result.body;
  });
  return { mock, calls };
}
