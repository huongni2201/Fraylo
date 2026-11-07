import { lazy, Suspense, useState } from "react";
import { PageHeader } from "@/components/PageHeader";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";

const FacebookAnalyticsPage = lazy(() => import("../facebook-analytics/FacebookAnalyticsPage").then(module => ({ default: module.FacebookAnalyticsPage })));

export function FacebookPageScreen({ active, onOpenAccounts, onOpenGroups, onOpenAi }: {
  active: boolean;
  onOpenAccounts: () => void;
  onOpenAi?: () => void;
  onOpenGroups: () => void;
}) {
  const [tab, setTab] = useState("posting");
  const [analyticsOpened, setAnalyticsOpened] = useState(false);

  return <section className="flex flex-col gap-6">
    <PageHeader title="Đăng bài Page" description="Quản lý bài đăng và theo dõi hiệu quả Fanpage Facebook."
      actions={<Button variant="outline" onClick={onOpenAccounts}>Tài khoản Facebook</Button>} />
    <Tabs value={tab} onValueChange={value => { setTab(value); if (value === "analytics") setAnalyticsOpened(true); }}>
      <TabsList aria-label="Đăng bài Page" className="mb-5">
        <TabsTrigger value="posting">Đăng bài</TabsTrigger>
        <TabsTrigger value="analytics">Analytics</TabsTrigger>
      </TabsList>
      <TabsContent value="posting" forceMount className={tab === "posting" ? "mt-0" : "hidden"}>
        <div className="rounded-xl border border-border bg-card p-6">
          <p className="font-medium">Chưa hỗ trợ đăng bài</p>
          <p className="mt-2 text-sm text-muted-foreground">Chức năng đăng bài lên Page chưa được triển khai. Bạn có thể tiếp tục sử dụng Đăng bài nhóm.</p>
          <Button className="mt-4" variant="outline" onClick={onOpenGroups}>Đăng bài nhóm</Button>
        </div>
      </TabsContent>
      <TabsContent value="analytics" forceMount className={tab === "analytics" ? "mt-0" : "hidden"}>
        {analyticsOpened && <Suspense fallback={<p role="status" className="text-sm text-muted-foreground">Đang tải Analytics…</p>}>
          <FacebookAnalyticsPage active={active && tab === "analytics"} onOpenAccounts={onOpenAccounts} onOpenAi={onOpenAi} embedded />
        </Suspense>}
      </TabsContent>
    </Tabs>
  </section>;
}
