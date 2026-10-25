package com.leonz03.bilicinema;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;
import android.os.Build;
import android.view.View;
import android.view.Window;

/** Shared light/dark palette and appearance preference for the native cinema UI. */
final class CinemaTheme {
    // Activity.getPreferences() delegates to getSharedPreferences(getLocalClassName(), MODE_PRIVATE).
    private static final String PREFERENCES = "MainActivity";
    private static final String APPEARANCE = "appearance";

    final int background, surface, border, text, muted, accent, success, warning;
    private final boolean dark;

    private CinemaTheme(boolean dark) {
        this.dark = dark;
        if (dark) {
            background = Color.rgb(15, 15, 15);
            surface = Color.rgb(33, 33, 33);
            border = Color.rgb(64, 64, 64);
            text = Color.rgb(241, 241, 241);
            muted = Color.rgb(170, 170, 170);
            accent = Color.rgb(62, 166, 255);
            success = Color.rgb(105, 190, 129);
            warning = Color.rgb(255, 190, 90);
        } else {
            background = Color.WHITE;
            surface = Color.rgb(248, 248, 248);
            border = Color.rgb(229, 229, 229);
            text = Color.rgb(15, 15, 15);
            muted = Color.rgb(96, 96, 96);
            accent = Color.rgb(6, 95, 212);
            success = Color.rgb(27, 125, 66);
            warning = Color.rgb(145, 83, 0);
        }
    }

    static CinemaTheme of(Context context, boolean forceDark) {
        return new CinemaTheme(forceDark || isDark(context));
    }

    static boolean isDark(Context context) {
        String appearance = preferences(context).getString(APPEARANCE, "system");
        if ("dark".equals(appearance)) return true;
        if ("light".equals(appearance)) return false;
        return (context.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK)
                == Configuration.UI_MODE_NIGHT_YES;
    }

    static void applySystemBars(Activity activity, boolean forceDark) {
        CinemaTheme theme = of(activity, forceDark);
        Window window = activity.getWindow();
        window.setStatusBarColor(theme.background);
        window.setNavigationBarColor(theme.background);
        if (Build.VERSION.SDK_INT >= 23) {
            int flags = activity.getWindow().getDecorView().getSystemUiVisibility();
            flags = theme.dark ? flags & ~View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
                    : flags | View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            if (Build.VERSION.SDK_INT >= 26) {
                flags = theme.dark ? flags & ~View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
                        : flags | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
            }
            activity.getWindow().getDecorView().setSystemUiVisibility(flags);
        }
    }

    static String appearanceLabel(Context context) {
        switch (preferences(context).getString(APPEARANCE, "system")) {
            case "light": return "浅色";
            case "dark": return "深色";
            default: return "跟随系统";
        }
    }

    static int materialDialogTheme(Context context) {
        return isDark(context) ? android.R.style.Theme_Material_Dialog_Alert
                : android.R.style.Theme_Material_Light_Dialog_Alert;
    }

    static int materialActivityTheme(Context context) {
        return isDark(context) ? android.R.style.Theme_Material_NoActionBar
                : android.R.style.Theme_Material_Light_NoActionBar;
    }

    static void showAppearanceDialog(Activity activity, Runnable onChanged) {
        String[] labels = {"跟随系统", "浅色", "深色"};
        String[] values = {"system", "light", "dark"};
        String current = preferences(activity).getString(APPEARANCE, "system");
        int selected = "light".equals(current) ? 1 : "dark".equals(current) ? 2 : 0;
        new AlertDialog.Builder(activity, materialDialogTheme(activity))
                .setTitle("外观")
                .setSingleChoiceItems(labels, selected, (dialog, which) -> {
                    if (!values[which].equals(current)) {
                        preferences(activity).edit().putString(APPEARANCE, values[which]).apply();
                        dialog.dismiss();
                        onChanged.run();
                    } else dialog.dismiss();
                })
                .setNegativeButton("取消", null)
                .show();
    }

    private static SharedPreferences preferences(Context context) {
        return context.getSharedPreferences(PREFERENCES, Context.MODE_PRIVATE);
    }
}
