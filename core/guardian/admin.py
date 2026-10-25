from django.contrib import admin

from .models import Event, Gate, Live, Opinion, RelayTrust, Request, ShareSetting, Slot


@admin.register(Event)
class EventAdmin(admin.ModelAdmin):
    list_display = ("at", "source", "kind", "text")
    list_filter = ("source", "kind")
    search_fields = ("text",)

    # 監査ログは画面からも直させない
    def has_change_permission(self, request, obj=None):
        return False

    def has_delete_permission(self, request, obj=None):
        return False


@admin.register(Opinion)
class OpinionAdmin(admin.ModelAdmin):
    list_display = ("at", "source", "text", "url")
    list_filter = ("source",)


@admin.register(Slot)
class SlotAdmin(admin.ModelAdmin):
    list_display = ("start", "end", "mode", "note")


admin.site.register([Live, Gate, RelayTrust, Request, ShareSetting])
admin.site.site_header = "ロボもり 中核（Robomori core）"
