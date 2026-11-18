package com.akashicrecords.data

import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Update

// ---- Liste de courses (phone only: the PC has the same table, declared in SchemaInitializer, but no screen) ----
//
// One line of the shopping checklist. A line read from a recipe keeps its amount apart from its name, so the
// same product coming from two recipes adds up: Quantity is in the base unit named by Unit ("g", "ml",
// "c. à s.", "c. à c.", or "" for things that are counted), and null when the recipe gives no amount
// ("Sel, poivre") or the line was typed by hand. Source lists the recipes the line comes from.
// Phone-owned: DesktopImport leaves the table alone.
@Entity(tableName = "ShoppingItem")
data class ShoppingItem(
    @PrimaryKey(autoGenerate = true) val Id: Int = 0,
    val Name: String,
    val Quantity: Double?,
    val Unit: String,
    val IsDone: Boolean,
    val Source: String,
    val CreatedAt: String,
)

@Dao
interface ShoppingItemDao {
    @Insert
    suspend fun add(i: ShoppingItem): Long

    @Update
    suspend fun update(i: ShoppingItem)

    @Query("SELECT * FROM ShoppingItem ORDER BY Id")
    suspend fun getAll(): List<ShoppingItem>

    @Query("DELETE FROM ShoppingItem WHERE IsDone = 1")
    suspend fun deleteDone()

    @Query("DELETE FROM ShoppingItem")
    suspend fun deleteAll()
}
