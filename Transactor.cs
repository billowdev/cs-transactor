using System;
using System.Threading;
using System.Threading.Tasks;
using App.Sources.Infra.Infra.Database; // Assuming this is your ApplicationDBContext namespace
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace App.Sources.Infra.Persistence.Transactor
{


	public interface IAtomicTransactor : IDisposable, IAsyncDisposable // Implement IAsyncDisposable
	{
		Task BeginTransactionAsync(CancellationToken cancellationToken = default);
		Task CommitAsync(CancellationToken cancellationToken = default);
		Task RollbackAsync(CancellationToken cancellationToken = default);
		Task SaveChangesAsync(CancellationToken cancellationToken = default); // Useful for intermediate saves within a transaction
		bool IsTransactionActive { get; } // Optional: To check if a transaction has been started
		ApplicationDBContext Context { get; } // Expose the DbContext
	}

	public class AtomicTransactor : IAtomicTransactor
	{
		private readonly ApplicationDBContext _context;
		private IDbContextTransaction? _transaction;
		private bool _disposed = false;
		private bool _completedTransaction = false; // Tracks if commit or rollback has been called

		public AtomicTransactor(ApplicationDBContext context)
		{
			_context = context ?? throw new ArgumentNullException(nameof(context));
		}

		public bool IsTransactionActive => _transaction != null && !_completedTransaction;

		public ApplicationDBContext Context => _context;  // Expose the context

		public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedCheck();
			if (_transaction != null)
			{
				throw new InvalidOperationException("A transaction is already in progress.");
			}
			_transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
			_completedTransaction = false; // Reset for the new transaction
		}

		public async Task CommitAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedCheck();
			TransactionNullCheck("Cannot commit a transaction that has not been started.");
			if (_completedTransaction)
			{
				throw new InvalidOperationException("Transaction has already been completed (committed or rolled back).");
			}

			try
			{
				// Save changes to the context before committing the database transaction
				await _context.SaveChangesAsync(cancellationToken);
				if (_transaction != null)
				{
					await _transaction.CommitAsync(cancellationToken);
				}
				else
				{
					throw new InvalidOperationException("Transaction is null and cannot be committed.");
				}
				_completedTransaction = true;
			}
			catch (Exception)
			{
				// Attempt to roll back if commit fails
				await RollbackAsyncInternal(cancellationToken); // Use internal to avoid redundant checks
				throw;
			}
		}

		public async Task RollbackAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedCheck();
			// Allow rollback even if transaction wasn't explicitly started by this instance,
			// or if it's already marked as completed, as a safeguard.
			// However, a strict approach might throw if _transaction is null or _completedTransaction is true.
			// For this example, we'll be more lenient on explicit calls to RollbackAsync.
			if (_transaction != null && !_completedTransaction)
			{
				await RollbackAsyncInternal(cancellationToken);
			}
		}

		private async Task RollbackAsyncInternal(CancellationToken cancellationToken = default)
		{
			// Internal helper to be called from CommitAsync's catch and DisposeAsync
			if (_transaction != null && !_completedTransaction) // Check again as state might change
			{
				try
				{
					await _transaction.RollbackAsync(cancellationToken);
				}
				finally // Ensure it's marked as completed even if RollbackAsync itself throws (unlikely for EF Core)
				{
					_completedTransaction = true;
				}
			}
		}


		public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedCheck();
			TransactionNullCheck("Cannot save changes when a transaction has not been started or is completed.");
			if (_completedTransaction)
			{
				throw new InvalidOperationException("Cannot save changes after the transaction has been completed.");
			}
			await _context.SaveChangesAsync(cancellationToken);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		protected virtual void Dispose(bool disposing)
		{
			if (!_disposed)
			{
				if (disposing)
				{
					// In synchronous dispose, we rely on the DbContextTransaction's own Dispose
					// to roll back if it's still active.
					// We cannot reliably call async methods here.
					if (_transaction != null && !_completedTransaction)
					{
						// EF Core's IDbContextTransaction.Dispose() will roll back
						// if the transaction was not committed.
						_transaction.Dispose();
						_completedTransaction = true;
					}
				}
				_transaction = null; // Clear the reference
				_disposed = true;
			}
		}

		public async ValueTask DisposeAsync() // Implement IAsyncDisposable
		{
			if (!_disposed)
			{
				if (_transaction != null && !_completedTransaction)
				{
					await RollbackAsyncInternal(); // Ensure rollback if not completed
				}

				if (_transaction != null)
				{
					await _transaction.DisposeAsync();
				}
				_transaction = null;
				_disposed = true;
			}
			GC.SuppressFinalize(this); // If a finalizer were present
		}

		private void ObjectDisposedCheck()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(nameof(AtomicTransactor));
			}
		}

		private void TransactionNullCheck(string message)
		{
			if (_transaction == null)
			{
				throw new InvalidOperationException(message);
			}
		}
	}
}





// // Example usage:
// public static class AtomicTransactorExtensions
// {
// 	public static async Task SeedAsync(this IAtomicTransactor transactor, CancellationToken cancellationToken)
// 	{
// 		await using (transactor)
// 		{
// 			await transactor.BeginTransactionAsync(cancellationToken);

// 			try
// 			{
// 				var dbContext = transactor.Context; // Access the DbContext via the property

// 				// Perform your seeding operations using dbContext

// 				// Example:
// 				if (!await dbContext.Set<SomeEntity>().AnyAsync(cancellationToken)) // Assuming you have an entity called SomeEntity
// 				{
// 					dbContext.Set<SomeEntity>().Add(new SomeEntity { Name = "Initial Value" });
// 					await transactor.SaveChangesAsync(cancellationToken); // Use SaveChangesAsync through the transactor
// 				}

// 				await transactor.CommitAsync(cancellationToken);
// 			}
// 			catch (Exception)
// 			{
// 				await transactor.RollbackAsync(cancellationToken);
// 				throw; // Re-throw the exception to be handled further up the call stack
// 			}
// 		}
// 	}
// }

// // Example Entity
// public class SomeEntity
// {
// 	public int Id { get; set; }
// 	public string? Name { get; set; }
// }

// // Extension to add SomeEntity to the DbContext (optional)
// public static class ApplicationDbContextExtensions
// {
// 	public static void AddSomeEntity(this ModelBuilder modelBuilder)
// 	{
// 		modelBuilder.Entity<SomeEntity>(entity =>
// 		{
// 			entity.HasKey(e => e.Id);
// 			entity.Property(e => e.Name).HasMaxLength(255);
// 		});
// 	}
// }

// // Example of configuring SomeEntity in your DbContext (in the OnModelCreating method):
// // protected override void OnModelCreating(ModelBuilder modelBuilder)
// // {
// //     base.OnModelCreating(modelBuilder);
// //     modelBuilder.AddSomeEntity();
// // }
