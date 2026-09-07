package com.routesync.cameracount.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** RouteSync palette, shared with the driver app and the web dashboard. */
object RsColor {
    val Navy = Color(0xFF1B2A56)
    val Teal = Color(0xFF2E9E8F)
    val TealBright = Color(0xFF3AB5A4)
    val Mint1 = Color(0xFFEAF6F1)
    val Mint2 = Color(0xFFD6EDE6)
    val Mint3 = Color(0xFFC7E8DD)
    val FieldBorder = Color(0xFFD9DEE6)
    val Muted = Color(0xFF9AA3B2)
    val Error = Color(0xFFE74C3C)
    val CardWhite = Color(0xFFFFFFFF)
}

private val RsScheme = lightColorScheme(
    primary = RsColor.Teal,
    onPrimary = Color.White,
    secondary = RsColor.Navy,
    background = RsColor.Mint2,
    surface = RsColor.CardWhite,
    onSurface = RsColor.Navy,
    error = RsColor.Error,
    outline = RsColor.FieldBorder
)

@Composable
fun RsTheme(content: @Composable () -> Unit) =
    MaterialTheme(colorScheme = RsScheme, content = content)

/**
 * Full-bleed mint gradient background, matching the driver app's sign-in screen.
 *
 * The driver app lays the same gradient at 160 degrees under a background image. There is
 * no image here, so the gradient carries the screen on its own.
 */
@Composable
fun RsBackground(content: @Composable BoxScope.() -> Unit) {
    Box(
        Modifier
            .fillMaxSize()
            .background(
                Brush.linearGradient(listOf(RsColor.Mint1, RsColor.Mint2, RsColor.Mint3))
            ),
        content = content
    )
}

/**
 * Two-tone RouteSync wordmark and tagline, the same mark the driver app uses.
 *
 * Sized and spaced to the driver app's sign-in header: 28sp at weight 800 with the
 * letters drawn slightly tighter, which is what stops the two halves reading as two
 * words.
 */
@Composable
fun RsWordmark(tagline: String) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Row {
            Text(
                "Route", fontSize = 28.sp, fontWeight = FontWeight.ExtraBold,
                color = RsColor.Navy, letterSpacing = (-0.5).sp
            )
            Text(
                "Sync", fontSize = 28.sp, fontWeight = FontWeight.ExtraBold,
                color = RsColor.Teal, letterSpacing = (-0.5).sp
            )
        }
        Text(tagline, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = RsColor.Muted)
    }
}

/**
 * White rounded card, matching the driver app's sign-in card.
 *
 * The shadow is the part that carries the family resemblance: a wide, soft, navy-tinted
 * drop rather than the tight grey Material puts under an elevated surface. Ambient and
 * spot colours are honoured from API 28; below that the platform draws its own grey and
 * the card simply looks flatter.
 */
@Composable
fun RsCard(content: @Composable ColumnScope.() -> Unit) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        Modifier
            .fillMaxWidth()
            .widthIn(max = 360.dp)
            .shadow(
                elevation = 18.dp,
                shape = shape,
                ambientColor = RsColor.Navy,
                spotColor = RsColor.Navy
            )
            .clip(shape)
            .background(RsColor.CardWhite)
            .padding(start = 24.dp, top = 28.dp, end = 24.dp, bottom = 26.dp),
        content = content
    )
}

/** The heading inside a card, matching the driver app's card title. */
@Composable
fun RsCardTitle(text: String) {
    Text(
        text,
        fontSize = 28.sp,
        fontWeight = FontWeight.ExtraBold,
        color = RsColor.Navy,
        letterSpacing = (-0.5).sp,
        modifier = Modifier.fillMaxWidth(),
        textAlign = androidx.compose.ui.text.style.TextAlign.Center
    )
}

/**
 * The bordered row every input sits in.
 *
 * The driver app draws a flat one pixel border at a ten pixel radius with the icon inside
 * it, and turns the border teal while the field has focus. Material's outlined field
 * notches its border around a floating label and animates the label into the gap, which
 * is the single thing that made this app look like a different product.
 */
@Composable
private fun RsFieldBox(
    focused: Boolean,
    error: Boolean = false,
    onClick: (() -> Unit)? = null,
    content: @Composable RowScope.() -> Unit
) {
    val border = when {
        error -> RsColor.Error
        focused -> RsColor.Teal
        else -> RsColor.FieldBorder
    }
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(RsColor.CardWhite)
            .border(if (focused || error) 2.dp else 1.dp, border, RoundedCornerShape(10.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        content = content
    )
}

/**
 * A single-line input in the driver app's shape.
 *
 * The placeholder stands in for a label, which is how the sign-in screen is laid out: a
 * label that floats into the border needs the border to have a gap in it, and this
 * design has none.
 */
@Composable
fun RsTextField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
    leading: (@Composable () -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
    isError: Boolean = false,
    visualTransformation: VisualTransformation = VisualTransformation.None,
    keyboardOptions: KeyboardOptions = KeyboardOptions.Default,
    keyboardActions: KeyboardActions = KeyboardActions.Default
) {
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()

    Column(modifier) {
        RsFieldBox(focused = focused, error = isError) {
            if (leading != null) leading()
            BasicTextField(
                value = value,
                onValueChange = onValueChange,
                singleLine = true,
                interactionSource = interaction,
                textStyle = TextStyle(
                    color = RsColor.Navy, fontSize = 15.sp, fontWeight = FontWeight.Medium
                ),
                cursorBrush = SolidColor(RsColor.Teal),
                visualTransformation = visualTransformation,
                keyboardOptions = keyboardOptions,
                keyboardActions = keyboardActions,
                modifier = Modifier.weight(1f).padding(vertical = 13.dp),
                decorationBox = { inner ->
                    if (value.isEmpty()) {
                        Text(placeholder, color = RsColor.Muted, fontSize = 15.sp)
                    }
                    inner()
                }
            )
            if (trailing != null) trailing()
        }
    }
}

/**
 * A read-only field that opens a menu, for choosing the bus.
 *
 * Built on the same box as the editable fields rather than on Material's exposed dropdown,
 * so a picker and an input are the same shape.
 */
@Composable
fun RsPickerField(
    display: String,
    placeholder: String,
    expanded: Boolean,
    onClick: () -> Unit,
    trailing: (@Composable () -> Unit)? = null
) {
    RsFieldBox(focused = expanded, onClick = onClick) {
        Text(
            display.ifEmpty { placeholder },
            color = if (display.isEmpty()) RsColor.Muted else RsColor.Navy,
            fontSize = 15.sp,
            fontWeight = if (display.isEmpty()) FontWeight.Normal else FontWeight.Medium,
            modifier = Modifier.weight(1f).padding(vertical = 13.dp)
        )
        if (trailing != null) trailing()
    }
}

/** Helper text under a field, in the driver app's small muted size. */
@Composable
fun RsHint(text: String, error: Boolean = false) {
    Text(
        text,
        color = if (error) RsColor.Error else RsColor.Muted,
        fontSize = 13.sp,
        modifier = Modifier.padding(top = 6.dp, start = 2.dp)
    )
}

/**
 * The filled action button, matching the driver app's sign-in button: full width, a ten
 * pixel radius rather than Material's pill, and the label at the same weight the rest of
 * the suite uses for an action.
 */
@Composable
fun RsPrimaryButton(
    text: String,
    enabled: Boolean = true,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = RsColor.Teal,
            contentColor = Color.White,
            // The driver app dims the whole button rather than greying it to another
            // colour, so a disabled action still reads as the action it will become.
            disabledContainerColor = RsColor.Teal.copy(alpha = 0.6f),
            disabledContentColor = Color.White.copy(alpha = 0.9f)
        ),
        elevation = ButtonDefaults.buttonElevation(0.dp, 0.dp, 0.dp, 0.dp, 0.dp),
        contentPadding = PaddingValues(vertical = 13.dp),
        modifier = modifier.fillMaxWidth()
    ) {
        Text(text, fontSize = 16.sp, fontWeight = FontWeight.Bold)
    }
}
