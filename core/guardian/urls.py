from django.urls import path

from . import views

urlpatterns = [
    path("", views.dashboard, name="dashboard"),
    path("login", views.login, name="login"),
    path("logout", views.logout, name="logout"),
    path("api/state", views.state, name="state"),
    path("api/layout", views.layout_view, name="layout"),
    path("api/drive", views.drive_list, name="drive"),
    path("api/drive/<str:stamp>", views.drive_item, name="drive_item"),
    path("api/drill", views.drill, name="drill"),
    path("api/share", views.share_view, name="share"),
    path("api/judge", views.judge_view, name="judge"),
    path("api/ask", views.ask_view, name="ask"),
    path("api/key", views.key_state, name="key"),
    path("healthz", views.healthz, name="healthz"),
]
